using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using VisionInspection.Core.Interfaces;
using VisionInspection.Core.Models;
using VisionInspection.Core.Services;
using VisionInspection.Core.ViewModels;
using VisionInspection.Modules.Detection;
using VisionInspection.Modules.SOP;
using VisionInspection.Modules.SOP.Models;
using VisionInspection.Modules.SOP.Services;
using VisionInspection.UI.Services;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisionInspection.UI.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly DetectionOverlayRenderer _overlayRenderer = new();
    private readonly SopAudioFeedback _audioFeedback = new();
    private readonly CameraFramePipeline _framePipeline;
    private readonly ROIManager _roiManager;
    private readonly CameraManager _cameraManager;
    private readonly YoloDetectionService _detectionService;
    private readonly ModelManager _modelManager;
    private SOPModule? _sopModule;
    private Task? _sopStartTask;
    private ModelInfo? _loadedModel;
    private bool _isDisposed = false;

    /// <summary>主检测当前是否运行在 GPU 上（界面切换开关的数据源）</summary>
    [ObservableProperty]
    private bool _useGpu;

    private HandPoseEstimationConfig? _savedHandPoseConfig;

    public HandPoseEstimationConfig GetHandPoseConfig()
        => (_savedHandPoseConfig ?? SOPModuleConfig.FromConfiguration(BuildSopConfig()).HandPoseEstimation).Copy();

    public async Task ApplySopConfigurationAsync(string yamlPath, HandPoseEstimationConfig config)
    {
        SopWorkflowPath = yamlPath;
        _savedHandPoseConfig = config.Copy();
        if (_sopModule == null) return;
        await _sopModule.UpdateHandPoseConfigAsync(_savedHandPoseConfig);
        if (IsSOPDetecting && File.Exists(yamlPath))
            await Task.Run(() => _sopModule.StartWorkflowFromYaml(yamlPath));
    }

    private static readonly object _logLock = new();
    private static readonly string _debugLogPath = AppPaths.Log("sop_frame_debug.log");

    private void DebugLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var logLine = $"[{timestamp}] {message}";

        Console.WriteLine(logLine);
        System.Diagnostics.Debug.WriteLine(logLine);

        if (LogThrottle.Allow("main_vm", 10))
        {
            lock (_logLock)
            {
                try
                {
                    File.AppendAllText(_debugLogPath, logLine + Environment.NewLine);
                }
                catch { }
            }
        }
    }

    public bool IsCameraConnected => _cameraManager.IsConnected;

    /// <summary>
    /// SOP模块实例（供视图订阅事件）
    /// </summary>
    public SOPModule? SOPModuleInstance => _sopModule;

    /// <summary>
    /// 当前登录用户名（操作人）。由 MainWindow 在登录后写入，并同步给 SOP 模块，
    /// 使 SOP 运行记录带上操作人，供数据看板按用户过滤。
    /// </summary>
    private string _currentOperator = "";
    public string CurrentOperator
    {
        get => _currentOperator;
        set
        {
            _currentOperator = value ?? "";
            if (_sopModule != null) _sopModule.CurrentOperator = _currentOperator;
        }
    }

    [ObservableProperty]
    private ROIEditorViewModel _roiEditorViewModel = null!;

    [ObservableProperty]
    private SKBitmap? _currentImage;

    [ObservableProperty]
    private SKBitmap? _detectionResultImage;

    // ===== 多相机画面 =====

    /// <summary>多相机画面集合（主相机在前，供主界面宫格布局）</summary>
    public ObservableCollection<CameraViewItem> CameraViews { get; } = new();

    /// <summary>画面宫格行数（1 路=1，2 路=1，3/4 路=2）</summary>
    [ObservableProperty]
    private int _gridRows = 1;

    /// <summary>画面宫格列数（1 路=1，2 路=2，3/4 路=2）</summary>
    [ObservableProperty]
    private int _gridColumns = 1;

    [ObservableProperty]
    private string _status = "就绪";

    /// <summary>底部状态栏显示的当前连接相机型号（未连接时为空）</summary>
    [ObservableProperty]
    private string _cameraModelText = "";

    [ObservableProperty]
    private string _sopStatus = "SOP模块未初始化";

    [ObservableProperty]
    private int _currentStep = 0;

    [ObservableProperty]
    private int _totalSteps = 0;

    [ObservableProperty]
    private string _loadedModelName = "未加载模型";

    [ObservableProperty]
    private bool _isModelLoaded = false;

    [ObservableProperty]
    private List<DetectedObject> _detectionResults = new();

    [ObservableProperty]
    private DetectedObject? _selectedDetectionResult;

    [ObservableProperty]
    private int _highConfidenceCount = 0;

    [ObservableProperty]
    private string _videoPath = "";

    [ObservableProperty]
    private bool _isVideoPlaying = false;

    [ObservableProperty]
    private long _currentFrameIndex = 0;

    [ObservableProperty]
    private long _totalFrames = 0;

    [ObservableProperty]
    private double _fps = 0;

    [ObservableProperty]
    private bool _isCameraGrabbing = false;

    [ObservableProperty]
    private bool _isRealTimeDetecting = false;

    [ObservableProperty]
    private double _inferenceFps = 0;

    // ⭐ SOP实时检测字段
    [ObservableProperty]
    private bool _isSOPDetecting = false;

    [ObservableProperty]
    private string _sopWorkflowPath = "";

    private string _lastDetectionError = "";
    private DateTime _lastFrameTime = DateTime.Now;
    private int _frameCount = 0;
    // 视频推理时"原始帧 + 检测框"的副本（自己创建，负责释放；解码器的原始帧缓冲不能释放）
    private SKBitmap? _videoDrawnImage;
    private DateTime _lastInferenceTime = DateTime.Now;
    private int _inferenceFrameCount = 0;

    // 实时检测平滑器，参数由当前模型配置决定。
    // 参数从当前加载的 ModelInfo 读取，模型管理对话框可调。
    private DetectionTrackSmoother _realtimeSmoother = new(ema: 0.2f, maxMissed: 5);

    // 实时检测结果缓存：最近一次平滑后的检测对象，由 OnCameraFrameGrabbed 在最新相机帧上叠加渲染。
    private List<DetectedObject>? _lastRealtimeObjects;
    private int _noRealtimeResultFrameCount;
    private const int MaxNoRealtimeResultFrames = 15; // 连续多少帧无新实时结果则清空缓存

    public MainViewModel()
    {
        _roiManager = new ROIManager();
        RoiEditorViewModel = new ROIEditorViewModel(_roiManager);
        _cameraManager = CameraManager.Instance;
        _detectionService = new YoloDetectionService();
        _modelManager = new ModelManager();

        // 订阅检测错误事件
        _detectionService.DetectionError += OnDetectionError;

        // 订阅相机图像事件
        _cameraManager.SlotStatusChanged += OnCameraSlotStatusChanged;
        _cameraManager.ConnectionStatusChanged += OnCameraConnectionStatusChanged;

        // 预览渲染线程：与相机回调线程解耦，按上限帧率消费
        _framePipeline = new CameraFramePipeline(_cameraManager, RenderPreview, ProcessCameraFramesAsync);
    }

    /// <summary>
    /// 初始化多相机画面布局（主窗口 Loaded 后调用，重建已连接槽位对应的画面格子）
    /// </summary>
    public void InitCameraLayout()
    {
        RebuildCameraLayout();
    }

    /// <summary>
    /// 预览渲染（在预览线程执行）：位图转换 + 叠加缓存结果 + 派发 UI + 触发实时检测。
    /// 内容与原 OnCameraFrameGrabbed 一致，只是不再占用相机回调线程。
    /// </summary>
    private void RenderPreview(CameraFrameEventArgs e)
    {
        // 检查应用程序是否仍在运行
        if (System.Windows.Application.Current == null || _isDisposed)
            return;

        var skBitmap = CameraFrameConverter.ToSKBitmap(e.ImageData);
        if (skBitmap == null) return;

        bool isPrimary = string.Equals(e.CameraId, CameraManager.PrimaryCameraId, StringComparison.OrdinalIgnoreCase);

        if (IsSOPDetecting || IsRealTimeDetecting)
            _framePipeline.EnqueueInference(e.CameraId, skBitmap);

        if (IsSOPDetecting)
        {
            try
            {
                using var canvas = new SKCanvas(skBitmap);
                _overlayRenderer.DrawSop(canvas, skBitmap.Info, e.CameraId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Preview] 叠加 SOP 结果异常: {ex.Message}");
            }
        }

        // 实时检测（仅主相机）：在最新帧上叠加缓存的实时检测结果
        if (isPrimary && IsRealTimeDetecting && _lastRealtimeObjects != null)
        {
            try
            {
                using var canvas = new SKCanvas(skBitmap);
                _overlayRenderer.DrawDetectedObjects(canvas, _lastRealtimeObjects, 32f);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OnCameraFrameGrabbed] 叠加实时结果异常: {ex.Message}");
            }

            _noRealtimeResultFrameCount++;
            if (_noRealtimeResultFrameCount >= MaxNoRealtimeResultFrames)
            {
                _lastRealtimeObjects = null;
                _noRealtimeResultFrameCount = 0;
            }
        }

        // 在UI线程更新对应画面（主相机同步到 ROI 编辑画面，避免阻塞相机采集线程）
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isDisposed) { skBitmap.Dispose(); return; }

            var item = CameraViews.FirstOrDefault(v => string.Equals(v.CameraId, e.CameraId, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                item.CurrentImage?.Dispose();
                item.CurrentImage = skBitmap;
                item.IsConnected = true;
            }
            else
            {
                skBitmap.Dispose();
                return;
            }

            if (isPrimary && RoiEditorViewModel != null)
            {
                RoiEditorViewModel.CurrentImage = skBitmap;
            }
        }));

    }

    /// <summary>
    /// 槽位状态变化：重建宫格布局
    /// </summary>
    private void OnCameraSlotStatusChanged(object? sender, CameraSlotEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isDisposed) return;
            RebuildCameraLayout();
        }));
    }

    /// <summary>
    /// 重建画面区。
    /// <para>
    /// 不变式：<b>主画面格子常驻</b>。它是"当前显示源的宿主"——相机帧、加载的图片、视频推理结果
    /// 都画在这一格（<see cref="RoiEditorViewModel"/> 的 CurrentImage）。其余格子才随已连接相机增减。
    /// </para>
    /// 之前 CameraViews 只由"已连接槽位"生成，没连相机时整块画面区被隐藏，
    /// 加载视频只更新了 CurrentImage 却没有宿主控件 → "上传成功但看不到"。
    /// </summary>
    private void RebuildCameraLayout()
    {
        var connected = _cameraManager.Slots.Where(s => s.IsConnected).ToList();

        // ① 主画面格子常驻（与是否连相机无关）
        var primary = CameraViews.FirstOrDefault(v => v.IsPrimary);
        if (primary == null)
        {
            primary = new CameraViewItem
            {
                CameraId = CameraManager.PrimaryCameraId,
                DisplayName = "主画面",
                IsPrimary = true
            };
            CameraViews.Insert(0, primary);
        }
        primary.IsConnected = connected.Any(c =>
            string.Equals(c.CameraId, primary.CameraId, StringComparison.OrdinalIgnoreCase));

        // ② 宫格尺寸按"显示格子数"算（没相机时就是 1x1 这一格）
        int count = CameraViews.Count;
        GridRows = count <= 2 ? 1 : 2;
        GridColumns = count == 1 ? 1 : 2;

        // ③ 移除已断开/已注销的副相机格子（主画面不参与移除）
        foreach (var removed in CameraViews.Where(v => !v.IsPrimary &&
                     !connected.Any(c => string.Equals(c.CameraId, v.CameraId, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            // ROI 编辑画面与主相机格子共用同一个 SKBitmap 实例：
            // 直接 Dispose 后 ROI 控件还会绘制它 → 原生 AccessViolation（进程闪退）。
            // 先解除 ROI 画面的引用，再释放。
            ReleaseSharedImage(removed.CurrentImage);
            CameraViews.Remove(removed);
        }

        // ④ 新增/更新已连接相机对应的格子（主相机复用常驻主画面）
        foreach (var slot in connected.OrderByDescending(s => s.IsPrimary).ThenBy(s => s.CameraId, StringComparer.OrdinalIgnoreCase))
        {
            var item = CameraViews.FirstOrDefault(v => string.Equals(v.CameraId, slot.CameraId, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                item = new CameraViewItem
                {
                    CameraId = slot.CameraId,
                    DisplayName = string.IsNullOrEmpty(slot.DisplayName) ? slot.CameraId : slot.DisplayName,
                    IsPrimary = slot.IsPrimary
                };
                CameraViews.Add(item);
            }
            item.IsConnected = slot.IsConnected;
        }

        UpdateCameraModelText();
    }

    /// <summary>
    /// 释放与 ROI 编辑画面共用的位图：先解除 ROI 画面的引用再 Dispose。
    /// 直接释放后 ROI 控件仍可能绘制它 → 原生 AccessViolationException（连接/断开相机时闪退）。
    /// </summary>
    private void ReleaseSharedImage(SKBitmap? bitmap)
    {
        if (bitmap == null) return;
        if (ReferenceEquals(RoiEditorViewModel.CurrentImage, bitmap))
            RoiEditorViewModel.CurrentImage = null;
        bitmap.Dispose();
    }

    /// <summary>
    /// 从已连接槽位聚合相机型号摘要（如 "📷 ModelA | ModelB"）
    /// </summary>
    private void UpdateCameraModelText()
    {
        var models = _cameraManager.Slots
            .Where(s => s.IsConnected && s.CameraInfo != null)
            .Select(s => !string.IsNullOrEmpty(s.CameraInfo!.Model) ? s.CameraInfo.Model : s.CameraInfo.Name)
            .Where(m => !string.IsNullOrEmpty(m))
            .Distinct()
            .ToList();

        CameraModelText = models.Count > 0 ? $"📷 {string.Join(" | ", models)}" : "";
    }

    /// <summary>
    /// 执行实时检测
    /// </summary>
    private Task ProcessCameraFramesAsync(Dictionary<string, CaptureFrame> frames)
    {
        if (IsSOPDetecting) return PerformSOPDetectionAsync(frames);
        if (IsRealTimeDetecting && frames.TryGetValue(CameraManager.PrimaryCameraId, out var primary))
            return PerformRealTimeDetectionAsync(primary.Image);
        return Task.CompletedTask;
    }

    private async Task PerformRealTimeDetectionAsync(SKBitmap bitmap)
    {
        try
        {

            // 首帧诊断：输出当前推理使用的模型信息
            if (_inferenceFrameCount == 0)
            {
                var classes = _detectionService.GetClasses();
                System.Diagnostics.Debug.WriteLine($"[RealTimeDetection] ====== 首帧推理诊断 ======");
                System.Diagnostics.Debug.WriteLine($"[RealTimeDetection] 已加载模型: {LoadedModelName}");
                System.Diagnostics.Debug.WriteLine($"[RealTimeDetection] 检测服务已初始化: {_detectionService.IsInitialized}");
                System.Diagnostics.Debug.WriteLine($"[RealTimeDetection] 实际类别数: {classes.Count}");
                if (classes.Count > 0)
                    System.Diagnostics.Debug.WriteLine($"[RealTimeDetection] 前5个类别: {string.Join(", ", classes.Take(5))}");
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // 执行检测：有ROI则检测ROI区域，无ROI则检测全图
            DetectionResult result;
            if (RoiEditorViewModel.ROIs.Count > 0)
            {
                // 有ROI，检测ROI区域
                var rois = RoiEditorViewModel.ROIs.Select(r => new ROIInfo
                {
                    Name = r.ROIName,
                    X = r.GetBoundingBox().Left,
                    Y = r.GetBoundingBox().Top,
                    Width = r.GetBoundingBox().Width,
                    Height = r.GetBoundingBox().Height,
                    ShapeType = (Core.Services.ShapeType)(int)r.ShapeType
                }).ToList();

                // 调试输出ROI信息
                foreach (var roi in rois)
                {
                    System.Diagnostics.Debug.WriteLine($"ROI: {roi.Name}, X={roi.X}, Y={roi.Y}, W={roi.Width}, H={roi.Height}");
                }

                result = await _detectionService.DetectAsync(bitmap, rois);
            }
            else
            {
                // 无ROI，检测全图
                result = await _detectionService.DetectAsync(bitmap);
            }

            // 首帧诊断：输出关键点是否被提取（确认 pose 绘制有数据可画）
            if (_inferenceFrameCount == 0)
            {
                var kpObj = result.Objects.FirstOrDefault(o => o.KeyPoints != null && o.KeyPoints.Count > 0);
                if (kpObj != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[RealTimeDetection] 带关键点的对象: {kpObj.ClassName}, 关键点数: {kpObj.KeyPoints!.Count}");
                    System.Diagnostics.Debug.WriteLine($"[RealTimeDetection] 首个关键点: X={kpObj.KeyPoints[0].X:F1}, Y={kpObj.KeyPoints[0].Y:F1}, Conf={kpObj.KeyPoints[0].Confidence:F2}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[RealTimeDetection] 注意: 本帧未检测到带关键点的对象（pose 骨骼不会显示）。对象总数={result.Objects.Count}");
                }
            }

            stopwatch.Stop();

            // 更新UI
            _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_isDisposed && IsRealTimeDetecting)
                {
                    // 时序平滑：消除逐帧 bbox 闪烁/抖动（pose 关键点与 seg 掩膜一并保留，关键点也做 EMA 平滑）
                    _realtimeSmoother.Update(result.Objects);
                    var smoothed = _realtimeSmoother.GetActiveObjects();

                    DetectionResults = smoothed;
                    HighConfidenceCount = smoothed.Count(o => o.Confidence >= 0.5);

                    // 缓存最近一次平滑结果，交由 OnCameraImageGrabbed 在最新相机帧上叠加渲染
                    // （与 SOP 保持同一架构，避免「无框原始帧」与「有框帧」交替造成的闪烁）
                    _lastRealtimeObjects = smoothed;
                    _noRealtimeResultFrameCount = 0;

                    // 计算推理FPS
                    _inferenceFrameCount++;
                    var elapsed = DateTime.Now - _lastInferenceTime;
                    if (elapsed.TotalSeconds >= 1)
                    {
                        InferenceFps = _inferenceFrameCount / elapsed.TotalSeconds;
                        _inferenceFrameCount = 0;
                        _lastInferenceTime = DateTime.Now;
                    }
                }
            }));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"实时检测异常: {ex.Message}");
        }
    }

    #region SOP实时检测

    /// <summary>
    /// 执行 SOP 实时检测（多相机批量帧，由推理 worker 调用）
    /// </summary>
    private async Task PerformSOPDetectionAsync(Dictionary<string, CaptureFrame> frames)
    {
        try
        {
            if (_sopModule == null)
            {
                return;
            }

            // 执行 SOP 检测（SOPModule 内部按相机串行推理，主相机推进状态机）
            var result = await _sopModule.ProcessAsync(frames);

            if (result is SOPModuleResult sopResult)
            {
                // 在 UI 线程更新（只更新数据字段，不再覆盖画面）
                // 关键修复：之前这里会用"推理旧帧 + 检测框"覆盖 CurrentImage，
                // 导致画面退回上一帧、看起来一卡一卡。
                // 现在改为：缓存 sopResult，让 OnCameraFrameGrabbed 收到下一帧新画面时再叠加渲染。
                _ = System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_isDisposed || !IsSOPDetecting) return;

                    _overlayRenderer.UpdateSopResult(sopResult);

                    // 更新状态栏
                    SopStatus = sopResult.StepResults.Message;
                    CurrentStep = sopResult.StepResults.CurrentStep;
                    TotalSteps = sopResult.StepResults.TotalSteps;

                    // 更新检测结果列表（SOP 输出已是领域类型，拷一份防止后续被替换）
                    DetectionResults = sopResult.Detections.ToList();

                    HighConfidenceCount = DetectionResults.Count(o => o.Confidence >= 0.5);

                    // 不再在此处覆盖 RoiEditorViewModel.CurrentImage：
                    // 让 OnCameraFrameGrabbed 在收到下一帧新画面时统一叠加渲染，
                    // 画面永远是"最新相机帧 + 最新检测结果"，消除卡顿和上一帧残留。

                    // 计算推理 FPS
                    _inferenceFrameCount++;
                    var elapsed = DateTime.Now - _lastInferenceTime;
                    if (elapsed.TotalSeconds >= 1)
                    {
                        InferenceFps = _inferenceFrameCount / elapsed.TotalSeconds;
                        _inferenceFrameCount = 0;
                        _lastInferenceTime = DateTime.Now;
                    }

                    Status = $"SOP检测中 | 步骤 {CurrentStep}/{TotalSteps} | " +
                             $"检测到 {DetectionResults.Count} 个对象 | " +
                             $"FPS: {InferenceFps:F1} | " +
                             $"耗时: {sopResult.ElapsedMs}ms";
                    PresentMonitoringAlerts(sopResult);
                }));
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消（包括TaskCanceledException），不输出错误
            DebugLog($"[SOPDetection] 检测被取消");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SOP实时检测异常: {ex.Message}");
        }
    }

    private void PresentMonitoringAlerts(SOPModuleResult result)
    {
        if (result.RegionAlert != null)
        {
            SopStatus = "⚠ 区域监控: 未戴安全帽！";
            Status = $"⚠ {result.RegionAlert}";
            _audioFeedback.PlayNg();
        }
        if (result.CrossCameraAlert is { } hit)
        {
            SopStatus = $"⚠ 跨相机规则: {hit.Rule.Name}";
            Status = $"⚠ 跨相机协同报警: {hit.Detail}";
            _audioFeedback.PlayNg();
            foreach (var condition in hit.Rule.Conditions)
                CameraViews.FirstOrDefault(v => string.Equals(v.CameraId, condition.CameraId,
                    StringComparison.OrdinalIgnoreCase))?.MarkAlert(5);
        }
    }

    #endregion

    /// <summary>
    /// 相机连接状态改变回调
    /// </summary>
    private void OnCameraConnectionStatusChanged(object? sender, bool isConnected)
    {
        // 检查应用程序是否仍在运行
        if (System.Windows.Application.Current == null || _isDisposed)
            return;

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (!_isDisposed)
            {
                if (isConnected)
                {
                    var cam = _cameraManager.CurrentCamera;
                    string model = !string.IsNullOrEmpty(cam?.Model) ? cam.Model
                                 : !string.IsNullOrEmpty(cam?.Name) ? cam.Name
                                 : "未知";
                    CameraModelText = $"📷 {model}";
                    Status = "相机已连接";
                }
                else
                {
                    CameraModelText = "";
                    Status = "相机已断开";
                }
            }
        });
    }

    private void OnDetectionError(object? sender, string e)
    {
        _lastDetectionError = e;
    }

    /// <summary>
    /// 切换主检测计算设备（GPU/CPU）。立即释放并重建主检测服务（实时相机检测的推理引擎），
    /// 同时同步 SOP 模块，并持久化到 configs/sop_config.json。
    /// </summary>
    [RelayCommand]
    private async Task ToggleGpuAsync()
    {
        bool next = !UseGpu;
        bool finalGpu = next;

        // 1) 切换主检测服务（实时相机检测走 _detectionService）
        if (_loadedModel != null && _detectionService.IsInitialized)
        {
            // 避免重建期间与正在处理的帧竞争
            if (IsRealTimeDetecting)
                StopRealTimeDetection();

            _loadedModel.UseGpu = next;
            if (await _detectionService.InitializeAsync(_loadedModel))
            {
                finalGpu = next;
            }
            else
            {
                // CUDA 初始化失败 → 回退 CPU
                finalGpu = false;
                _loadedModel.UseGpu = false;
                await _detectionService.InitializeAsync(_loadedModel);
            }
        }

        // 2) 同步 SOP 模块（若已初始化）
        if (_sopModule != null)
        {
            finalGpu = await _sopModule.SetUseGpuAsync(finalGpu);
        }

        UseGpu = finalGpu;
        SaveUseGpuToConfig(finalGpu);
        Status = finalGpu ? "主检测已切换到 GPU (CUDA)" : "主检测运行在 CPU";
    }

    /// <summary>把主检测的 UseGpu 选择写回运行时配置文件 configs/sop_config.json</summary>
    private void SaveUseGpuToConfig(bool useGpu)
    {
        try
        {
            var path = AppPaths.Config("sop_config.json");
            if (!File.Exists(path)) return;
            var text = File.ReadAllText(path);
            var node = JsonNode.Parse(text);
            if (node?["SOPModule"] is JsonObject sop)
            {
                sop["UseGpu"] = useGpu;
                File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MainViewModel] 保存 UseGpu 到配置失败: {ex.Message}");
        }
    }

    /// <summary>启动流程复用同一模块实例及一次事件订阅。</summary>
    private async Task EnsureSopModuleAsync()
    {
        if (_sopModule != null) return;
        var module = new SOPModule();
        try
        {
            await module.InitializeAsync(BuildSopConfig());
            if (_savedHandPoseConfig != null)
                await module.UpdateHandPoseConfigAsync(_savedHandPoseConfig);
        }
        catch
        {
            module.Dispose();
            throw;
        }
        _sopModule = module;
        module.CurrentOperator = _currentOperator;
        UseGpu = module.IsUsingGpu;
        module.UseGpuChanged += OnSopUseGpuChanged;
        module.GpuSwitchFailed += OnSopGpuSwitchFailed;
        module.StepChanged += OnSOPStepChanged;
        module.StepCompleted += OnSOPStepCompleted;
        module.ViolationDetected += OnSOPViolationDetected;
        module.WorkflowCompleted += OnSOPWorkflowCompleted;
        module.ModelWarning += OnSOPModelWarning;
    }

    private void OnSopUseGpuChanged(object? sender, bool gpu)
        => Application.Current?.Dispatcher.BeginInvoke(new Action(() => UseGpu = gpu));

    private void OnSopGpuSwitchFailed(object? sender, EventArgs e)
        => Application.Current?.Dispatcher.BeginInvoke(new Action(() => Status = "⚠️ 当前设备 CUDA 不可用，已回退到 CPU 运行"));

    /// <summary>
    /// 构建 SOP 模块配置：唯一来源是 configs/sop_config.json（缺失时为空配置，
    /// 由 <see cref="SOPModule"/> 内部默认值兜底）。UI 不再用 AddInMemoryCollection 覆盖手部参数。
    /// </summary>
    private static IConfigurationRoot BuildSopConfig()
        => new ConfigurationBuilder()
            .AddJsonFile(AppPaths.Config("sop_config.json"), optional: true)
            .Build();

    /// <summary>
    /// 对当前相机画面执行一次 SOP 判定（单次，不启动连续检测）。
    /// <para>
    /// 帧来源 = 各已连接相机槽位的最新帧；没有相机帧时明确提示，不再用合成图伪造结果。
    /// 原实现里既调用了不带 ExtInfo 的 <c>ConnectAsync</c>（海康必失败），又用
    /// <c>CreateTestImage()</c> 画了几个图形去跑判定，容易让人误以为相机在工作。
    /// </para>
    /// </summary>
    [RelayCommand]
    public async Task RunSOPDetectionAsync()
    {
        if (_sopModule == null)
        {
            Status = "SOP模块未初始化";
            return;
        }

        var frames = new Dictionary<string, CaptureFrame>();
        var grabbed = new List<SKBitmap>();

        try
        {
            IsBusy = true;
            Status = "运行SOP检测...";

            foreach (var slot in _cameraManager.Slots.Where(s => s.IsConnected))
            {
                var bitmap = slot.LatestImageData is { } imageData
                    ? CameraFrameConverter.ToSKBitmap(imageData)
                    : null;
                if (bitmap == null) continue;

                grabbed.Add(bitmap);
                frames[slot.CameraId] = new CaptureFrame
                {
                    CameraId = slot.CameraId,
                    Image = bitmap,
                    Timestamp = DateTime.Now,
                    FrameNumber = _frameCount++
                };
            }

            if (frames.Count == 0)
            {
                Status = "没有可用的相机画面：请先在「相机」窗口连接相机并开始采集";
                return;
            }

            var result = await Task.Run(() => _sopModule.ProcessAsync(frames));
            if (!result.Success)
            {
                // 常见：工作流未启动（"SOP未在运行状态"）、没有主相机帧
                Status = $"SOP判定未执行：{result.ErrorMessage}";
                return;
            }

            if (result is SOPModuleResult sopResult)
            {
                SopStatus = sopResult.StepResults.Message;
                CurrentStep = sopResult.StepResults.CurrentStep;
                TotalSteps = sopResult.StepResults.TotalSteps;
                // 单次判定也要更新结果列表，否则界面看不出跑过
                DetectionResults = sopResult.Detections.ToList();
                HighConfidenceCount = DetectionResults.Count(o => o.Confidence >= 0.5);
                PresentMonitoringAlerts(sopResult);
            }

            Status = $"SOP检测完成（{frames.Count} 路相机）";
        }
        catch (Exception ex)
        {
            Status = $"错误: {ex.Message}";
        }
        finally
        {
            // 帧只在本方法内使用：ProcessAsync 为同步完成（返回 Task.FromResult），
            // 且录制器已在检测线程内编码拷贝，这里可以安全释放
            foreach (var bmp in grabbed) bmp.Dispose();
            IsBusy = false;
        }
    }

    #region SOP实时检测命令

    /// <summary>
    /// 初始化并启动 SOP 实时检测
    /// </summary>
    [RelayCommand]
    public Task StartSOPDetectionAsync()
        => _sopStartTask is { IsCompleted: false } ? _sopStartTask : _sopStartTask = StartSopDetectionCoreAsync();

    private async Task StartSopDetectionCoreAsync()
    {
        try
        {
            IsBusy = true;

            // 1. 检查相机（至少一路已连接且在采集）
            if (!_cameraManager.Slots.Any(s => s.IsConnected))
            {
                MessageBox.Show("请先连接相机", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!_cameraManager.Slots.Any(s => s.IsGrabbing))
            {
                MessageBox.Show("请先开始相机采集", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Status = "正在初始化 SOP 模块...";

            await EnsureSopModuleAsync();

            // 3. 确保手部姿态估计服务已准备好（等待异步初始化完成）
            Console.WriteLine($"[MainViewModel] 确保手部服务准备就绪...");
            await _sopModule!.EnsureHandPoseServiceReadyAsync();

            // 3.5 模型缺失时明确提示（否则表现是"检测不到任何目标"，很难自查）
            if (!_sopModule!.IsModelLoaded)
            {
                MessageBox.Show(
                    "SOP 物体检测模型未加载：找不到 ONNX 模型文件。\n\n" +
                    "请把模型放到 models/ 或 yolo_models/（文件名需与配方 model.path 一致），\n" +
                    "启动时也会在 logs/startup.log 记录实际查找的目录。",
                    "SOP 模型缺失", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // 4. 加载工作流（优先从YAML，否则使用空工作流）
            Status = "正在启动检测...";
            DebugLog($"[StartSOP] 正在加载工作流...");
            if (!string.IsNullOrEmpty(SopWorkflowPath) && File.Exists(SopWorkflowPath))
            {
                DebugLog($"[StartSOP] 从YAML加载工作流: {SopWorkflowPath}");
                await Task.Run(() => _sopModule!.StartWorkflowFromYaml(SopWorkflowPath));
            }
            else
            {
                DebugLog($"[StartSOP] YAML路径为空或文件不存在，使用空工作流");
                var workflow = new SOPWorkflow
                {
                    Id = "default",
                    Name = "实时检测",
                    Description = "基于配置的实时检测工作流",
                    Steps = new List<SOPStep>(),
                    Regions = new List<ZoneDefinition>()
                };
                await Task.Run(() => _sopModule!.StartWorkflow(workflow));
            }
            DebugLog($"[StartSOP] StartWorkflow 完成");

            // 安全帽监控任务状态提示
            var monitorCfg = _sopModule?.CurrentWorkflow?.Monitoring;
            if (monitorCfg != null && monitorCfg.Enabled)
            {
                var zoneCount = _sopModule?.CurrentWorkflow?.Regions?.Count ?? 0;
                DebugLog($"[StartSOP] 安全帽监控任务已启用: person={string.Join(",", monitorCfg.PersonClasses)} | helmet={string.Join(",", monitorCfg.HelmetClasses)} | 标定区域数={zoneCount}");
                if (zoneCount == 0)
                    DebugLog("[StartSOP] 警告: 未标定任何区域，安全帽监控将不生效（请用「🎯 标定区域」定义）");
            }
            else
            {
                DebugLog("[StartSOP] 当前配方未启用安全帽监控（仅按 SOP 步骤任务运行）");
            }

            // 5. 启动实时检测
            IsSOPDetecting = true;
            DebugLog($"[StartSOP] IsSOPDetecting 设置为: {IsSOPDetecting}");
            IsRealTimeDetecting = false; // 关闭通用检测，避免冲突
            _inferenceFrameCount = 0;
            _lastInferenceTime = DateTime.Now;

            // 修复：启动推理工作线程
            _framePipeline.StartInference();
            DebugLog($"[StartSOP] 推理工作线程已启动");

            SopStatus = "SOP 实时检测运行中";
            Status = "SOP 实时检测已启动";
            DebugLog($"[StartSOP] SOP启动完成，等待相机帧...");
        }
        catch (Exception ex)
        {
            SopStatus = $"启动失败: {ex.Message}";
            Status = $"SOP 启动错误: {ex.Message}";
            MessageBox.Show($"SOP 启动失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 停止 SOP 实时检测 - 修复：停止推理工作线程
    /// </summary>
    [RelayCommand]
    public async Task StopSOPDetectionAsync()
    {
        IsSOPDetecting = false;

        // 修复：停止推理工作线程
        await _framePipeline.StopInferenceAsync();
        DebugLog($"[StopSOP] 推理工作线程已停止");

        _sopModule?.StopWorkflow();
        // 清空画面违规横幅缓存（停止检测即不再提示）
        _overlayRenderer.ResetSop();
        // 清空多相机检测结果缓存与告警标记
        foreach (var item in CameraViews)
        {
            item.IsAlerting = false;
        }
        // 重置区域监控报警状态
        InferenceFps = 0;
        SopStatus = "SOP 检测已停止";
        Status = "SOP 检测已停止";
    }

    /// <summary>
    /// 重置当前 SOP 工作流（从第一步重新开始）
    /// </summary>
    [RelayCommand]
    public void ResetSOPWorkflow()
    {
        _sopModule?.ResetWorkflow();
        // 复位即清空画面违规横幅（新一轮流程从干净状态开始）
        _overlayRenderer.ResetSop();
        SopStatus = "SOP 工作流已重置";
        Status = "SOP 工作流已重置，从第一步重新开始";
    }

    /// <summary>
    /// 切换 SOP 工作流（选择另一个 YAML）
    /// </summary>
    [RelayCommand]
    public async Task SwitchSOPWorkflowAsync()
    {
        // 先停止当前检测
        var wasDetecting = IsSOPDetecting;
        if (wasDetecting) await StopSOPDetectionAsync();

        // 清空路径，让 StartSOPDetectionAsync 重新弹文件选择
        SopWorkflowPath = "";

        // 重新启动
        if (wasDetecting)
        {
            await StartSOPDetectionAsync();
        }
    }

    // --- SOP 事件处理 ---

    private void OnSOPStepChanged(object? sender, StepChangedEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isDisposed) return;
            CurrentStep = e.CurrentStepId;
            SopStatus = $"步骤 {e.CurrentStepId}: {e.StepName}";
            Status = $"SOP 步骤推进: {e.PreviousStepId} → {e.CurrentStepId} ({e.StepName})";
            // 每步完成后播放 ok.wav（初始 0→1 是"开始"，不算完成，不播）
            if (e.PreviousStepId != 0)
                _audioFeedback.PlayOk();
        }));
    }

    private void OnSOPViolationDetected(object? sender, ViolationEventArgs e)
    {
        Console.WriteLine($"[SOP-Audio][DEBUG] OnSOPViolationDetected 触发: type={e.Violation.Type} desc={e.Violation.Description} | Application.Current={(System.Windows.Application.Current == null ? "NULL(不播)" : "OK")}");
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isDisposed) return;

            _overlayRenderer.AddViolation(e.Violation);

            SopStatus = $"⚠ 违规: {e.Violation.Description}";
            Status = $"⚠ SOP 违规: [{e.Violation.Type}] {e.Violation.Description}";
            // 跳步（中间跳了工序）→ 播放 ng.wav
            if (e.Violation.Type == ViolationType.SkipStep)
            {
                _audioFeedback.PlayNg();
            }
        }));
    }

    /// <summary>
    /// 步骤完成：本步无违规（正常走完）→ 清空画面违规横幅。
    /// 语义：违规提示跟随"出问题的那一步"，该步骤被正常完成后流程恢复正常，红提示消失；
    /// 若该步仍带违规（如超时后勉强完成）则横幅继续保留。
    /// </summary>
    private void OnSOPStepCompleted(object? sender, StepCompletedEventArgs e)
    {
        if (e.HasViolations) return;

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isDisposed) return;
            _overlayRenderer.ClearViolations();
        }));
    }

    private void OnSOPWorkflowCompleted(object? sender, SOPCompletedEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isDisposed) return;
            var isPass = !e.HasViolations;
            if (isPass)
            {
                // 整个流程无违规 → 清空横幅（兜底，正常情况下步骤完成时已清）
                _overlayRenderer.ClearViolations();
            }
            SopStatus = isPass ? "✅ SOP 全部通过" : "❌ SOP 检测不通过";
            Status = isPass
                ? $"SOP 完成: 全部 {TotalSteps} 步通过"
                : $"SOP 完成: 有 {e.Violations.Count} 个违规";
            // 最后一步完成也播放 ok.wav
            _audioFeedback.PlayOk();
        }));
    }

    /// <summary>
    /// 模型加载告警：专用模型缺失/回退时弹窗提示，避免"什么都没识别到却无提示"
    /// </summary>
    private void OnSOPModelWarning(object? sender, string message)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isDisposed) return;
            SopStatus = "⚠ 模型告警: " + message;
            Status = "⚠ SOP 模型告警（见弹窗）";
            MessageBox.Show(message, "SOP 模型告警", MessageBoxButton.OK, MessageBoxImage.Warning);
        }));
    }

    #endregion

    [RelayCommand]
    public async Task LoadImageAsync()
    {
        try
        {
            IsBusy = true;
            Status = "加载图像...";

            // 打开文件选择对话框
            var openFileDialog = new OpenFileDialog
            {
                Title = "选择图像文件",
                Filter = "图像文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif|所有文件|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            };

            if (openFileDialog.ShowDialog() == true)
            {
                await Task.Run(() =>
                {
                    using var stream = new FileStream(openFileDialog.FileName, FileMode.Open, FileAccess.Read);
                    CurrentImage = SKBitmap.Decode(stream);
                });

                RoiEditorViewModel.CurrentImage = CurrentImage;
                Status = $"图像加载成功: {Path.GetFileName(openFileDialog.FileName)}";
            }
            else
            {
                Status = "取消加载图像";
            }
        }
        catch (Exception ex)
        {
            Status = $"错误: {ex.Message}";
            MessageBox.Show($"加载图像失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void SaveROIs()
    {
        try
        {
            IsBusy = true;
            Status = "保存ROI...";

            // 这里应该将ROI保存到配置文件
            var rois = _roiManager.ROIs;
            // 序列化ROIs到JSON

            Status = $"ROI保存成功，共 {rois.Count} 个";
        }
        catch (Exception ex)
        {
            Status = $"错误: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void ClearROIs()
    {
        RoiEditorViewModel.ClearAllROIsCommand.Execute(null);
        Status = "ROI已清空";
    }

    #region YOLO检测功能

    [RelayCommand]
    public async Task LoadModelAsync()
    {
        try
        {
            // 如果正在视频推理，先停止
            if (IsVideoPlaying)
            {
                Status = "正在停止视频推理...";
                StopVideoInference();
                // 等待一小段时间确保资源释放
                await Task.Delay(500);
            }

            IsBusy = true;
            Status = "加载模型...";

            // 打开模型选择对话框
            var dialog = new Views.ModelSelectionDialog();
            if (Application.Current.MainWindow != null)
            {
                dialog.Owner = Application.Current.MainWindow;
            }

            var result = dialog.ShowDialog();
            if (result != true)
            {
                Status = "取消加载模型";
                return;
            }

            _loadedModel = dialog.SelectedModel;
            if (_loadedModel == null)
            {
                Status = "未选择模型";
                return;
            }

            // 诊断日志：输出选中模型的完整信息
            System.Diagnostics.Debug.WriteLine($"[LoadModel] ====== 模型加载开始 ======");
            System.Diagnostics.Debug.WriteLine($"[LoadModel] 选中模型名称: {_loadedModel.Name}");
            System.Diagnostics.Debug.WriteLine($"[LoadModel] 模型文件路径: {_loadedModel.ModelPath}");
            System.Diagnostics.Debug.WriteLine($"[LoadModel] 文件是否存在: {File.Exists(_loadedModel.ModelPath)}");
            System.Diagnostics.Debug.WriteLine($"[LoadModel] 模型类型: {_loadedModel.Type}");
            System.Diagnostics.Debug.WriteLine($"[LoadModel] 使用GPU: {_loadedModel.UseGpu}, GPU ID: {_loadedModel.GpuId}");
            System.Diagnostics.Debug.WriteLine($"[LoadModel] 置信度阈值: {_loadedModel.ConfidenceThreshold}, IoU阈值: {_loadedModel.IouThreshold}");
            System.Diagnostics.Debug.WriteLine($"[LoadModel] 平滑参数: EMA={_loadedModel.SmoothEma:F2}, Confirm={_loadedModel.SmoothConfirmHits}, MaxMissed={_loadedModel.SmoothMaxMissed}, Iou={_loadedModel.SmoothIouThreshold:F2}");

            // 初始化检测服务
            if (await _detectionService.InitializeAsync(_loadedModel))
            {
                IsModelLoaded = true;
                LoadedModelName = _loadedModel.Name;

                // 根据当前模型配置的平滑参数重建实时检测平滑器
                _realtimeSmoother = new DetectionTrackSmoother(
                    iouThreshold: _loadedModel.SmoothIouThreshold,
                    ema: _loadedModel.SmoothEma,
                    confirmHits: _loadedModel.SmoothConfirmHits,
                    maxMissed: _loadedModel.SmoothMaxMissed);

                var actualClasses = _detectionService.GetClasses();
                Status = $"模型加载成功: {_loadedModel.Name} | 路径: {_loadedModel.ModelPath} | 实际类别数: {actualClasses.Count}";
                System.Diagnostics.Debug.WriteLine($"[LoadModel] 初始化成功! 实际类别数: {actualClasses.Count}");
                if (actualClasses.Count > 0)
                    System.Diagnostics.Debug.WriteLine($"[LoadModel] 前5个类别: {string.Join(", ", actualClasses.Take(5))}");
            }
            else
            {
                // 修复：加载失败时必须重置 IsModelLoaded，否则会残留上一次成功加载的状态
                IsModelLoaded = false;
                LoadedModelName = "未加载模型";
                Status = $"模型加载失败: {_loadedModel.Name} | 路径: {_loadedModel.ModelPath}";
                System.Diagnostics.Debug.WriteLine($"[LoadModel] 初始化失败! 路径: {_loadedModel.ModelPath}");
            }
        }
        catch (Exception ex)
        {
            Status = $"加载模型失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task RunDetectionAsync()
    {
        try
        {
            if (!IsModelLoaded)
            {
                MessageBox.Show("请先加载模型", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (CurrentImage == null)
            {
                MessageBox.Show("请先加载图像", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsBusy = true;
            Status = "正在检测...";

            // 执行检测
            var result = await _detectionService.DetectAsync(CurrentImage);

            // 保存检测结果
            DetectionResults = result.Objects;
            HighConfidenceCount = result.Objects.Count(o => o.Confidence >= 0.5);

            // 绘制检测结果到图像
            DetectionResultImage = _overlayRenderer.DrawDetectionResults(CurrentImage, result.Objects);

            // 更新显示
            RoiEditorViewModel.CurrentImage = DetectionResultImage;

            Status = $"检测完成，发现 {result.Objects.Count} 个对象，耗时 {result.ProcessingTimeMs:F1}ms";
        }
        catch (Exception ex)
        {
            Status = $"检测错误: {ex.Message}";
            MessageBox.Show($"检测失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void ClearDetectionResults()
    {
        DetectionResults.Clear();
        HighConfidenceCount = 0;
        DetectionResultImage = null;
        if (CurrentImage != null)
        {
            RoiEditorViewModel.CurrentImage = CurrentImage;
        }
        Status = "检测结果已清除";
    }

    [RelayCommand]
    public void CopyDetectionResults()
    {
        if (DetectionResults.Count == 0)
        {
            Status = "没有检测结果可复制";
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("类别\t置信度\t位置X\t位置Y\t宽度\t高度");
        foreach (var obj in DetectionResults)
        {
            sb.AppendLine($"{obj.ClassName}\t{obj.Confidence:P2}\t{obj.BoundingBox[0]:F1}\t{obj.BoundingBox[1]:F1}\t{obj.BoundingBox[2]:F1}\t{obj.BoundingBox[3]:F1}");
        }
        Clipboard.SetText(sb.ToString());
        Status = $"已复制 {DetectionResults.Count} 条检测结果到剪贴板";
    }

    [RelayCommand]
    public void ExportDetectionResults()
    {
        if (DetectionResults.Count == 0)
        {
            Status = "没有检测结果可导出";
            return;
        }

        var saveFileDialog = new SaveFileDialog
        {
            Title = "导出检测结果",
            Filter = "CSV文件 (*.csv)|*.csv|文本文件 (*.txt)|*.txt",
            FileName = $"检测结果_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("类别,置信度,位置X,位置Y,宽度,高度");
                foreach (var obj in DetectionResults)
                {
                    sb.AppendLine($"{obj.ClassName},{obj.Confidence:F4},{obj.BoundingBox[0]:F2},{obj.BoundingBox[1]:F2},{obj.BoundingBox[2]:F2},{obj.BoundingBox[3]:F2}");
                }
                File.WriteAllText(saveFileDialog.FileName, sb.ToString());
                Status = $"检测结果已导出到: {saveFileDialog.FileName}";
            }
            catch (Exception ex)
            {
                Status = $"导出失败: {ex.Message}";
            }
        }
    }

    #endregion

    #region 视频推理功能

    [RelayCommand]
    public async Task LoadVideoAsync()
    {
        try
        {
            var openFileDialog = new OpenFileDialog
            {
                Title = "选择视频文件",
                Filter = "视频文件|*.mp4;*.avi;*.mkv;*.mov;*.wmv|所有文件|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
            };

            if (openFileDialog.ShowDialog() == true)
            {
                VideoPath = openFileDialog.FileName;
                Status = $"正在加载视频: {Path.GetFileName(VideoPath)}...";

                // 加载并显示视频第一帧（与视频推理共用同一个解码/转换路径）
                await Task.Run(() =>
                {
                    try
                    {
                        using var videoCapture = new OpenCvSharp.VideoCapture(VideoPath);
                        if (!videoCapture.IsOpened())
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                Status = $"无法解码视频文件（编码不受支持？）: {Path.GetFileName(VideoPath)}";
                            });
                            return;
                        }

                        using var frame = new OpenCvSharp.Mat();
                        if (videoCapture.Read(frame) && !frame.Empty())
                        {
                            var skBitmap = VideoFrameConverter.ToSKBitmap(frame);

                            // 在UI线程更新图像
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                CurrentImage = skBitmap;
                                RoiEditorViewModel.CurrentImage = skBitmap;
                                Status = $"视频已加载: {Path.GetFileName(VideoPath)}";
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            Status = $"加载视频预览失败: {ex.Message}";
                        });
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Status = $"加载视频失败: {ex.Message}";
            MessageBox.Show($"加载视频失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    public async Task StartVideoInferenceAsync()
    {
        try
        {
            if (!IsModelLoaded)
            {
                MessageBox.Show("请先加载模型", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(VideoPath))
            {
                MessageBox.Show("请先加载视频", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 重复点击"视频推理"：先停掉上一轮（顺带取消事件订阅，避免同一处理器被订阅两次）
            if (IsVideoPlaying)
                StopVideoInference();

            IsBusy = true;
            IsVideoPlaying = true;
            Status = "正在初始化视频推理...";

            // 初始化视频推理
            var options = new VideoInferenceOptions
            {
                VideoPath = VideoPath,
                FrameInterval = 0,  // 处理所有帧
                StartTimeSeconds = 0,
                DurationSeconds = 0
            };

            // 清空之前的错误信息
            _lastDetectionError = "";

            if (!_detectionService.InitializeVideoInference(options))
            {
                IsVideoPlaying = false;

                // 视频解码现在由程序内置的 OpenCV 完成，不再依赖外部 ffmpeg/ffprobe
                Status = $"视频推理初始化失败: {_lastDetectionError}";
                MessageBox.Show(
                    $"视频推理初始化失败:\n{_lastDetectionError}\n\n" +
                    "视频解码使用程序内置 OpenCV（不需要外部 ffmpeg）。\n" +
                    "若视频文件本身损坏或编码不受支持，请先转成 MP4(H.264) 再试。",
                    "视频推理初始化失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 订阅视频帧检测事件
            _detectionService.VideoFrameDetected += OnVideoFrameDetected;
            _detectionService.VideoInferenceCompleted += OnVideoInferenceCompleted;

            Status = "开始视频推理...";
            _detectionService.StartVideoInference();
        }
        catch (Exception ex)
        {
            Status = $"视频推理错误: {ex.Message}";
            MessageBox.Show($"视频推理失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            IsVideoPlaying = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void StopVideoInference()
    {
        try
        {
            _detectionService.StopVideoInference();
            _detectionService.VideoFrameDetected -= OnVideoFrameDetected;
            _detectionService.VideoInferenceCompleted -= OnVideoInferenceCompleted;
            IsVideoPlaying = false;
            Status = "视频推理已停止";
        }
        catch (Exception ex)
        {
            Status = $"停止视频推理失败: {ex.Message}";
        }
    }

    private void OnVideoFrameDetected(object? sender, VideoFrameResult e)
    {
        // 在UI线程更新
        Application.Current.Dispatcher.Invoke(() =>
        {
            CurrentFrameIndex = e.FrameIndex;
            DetectionResults = e.DetectionResult.Objects;
            HighConfidenceCount = e.DetectionResult.Objects.Count(o => o.Confidence >= 0.5);

            // 计算FPS
            _frameCount++;
            var now = DateTime.Now;
            var elapsed = now - _lastFrameTime;
            if (elapsed.TotalSeconds >= 1)
            {
                Fps = Math.Round(_frameCount / elapsed.TotalSeconds, 1);
                _frameCount = 0;
                _lastFrameTime = now;
            }

            // 绘制检测结果。e.Frame 是解码器复用的缓冲（不能释放）；
            // 自己画出来的副本必须释放上一张，否则分辨率 1080p 时每帧泄漏 8MB。
            var drawn = e.DetectionResult.Objects.Count > 0
                ? _overlayRenderer.DrawDetectionResults(e.Frame, e.DetectionResult.Objects)
                : null;

            var previous = _videoDrawnImage;
            _videoDrawnImage = drawn;
            RoiEditorViewModel.CurrentImage = drawn ?? e.Frame;
            previous?.Dispose();

            Status = $"处理帧 {e.FrameIndex}，检测到 {e.DetectionResult.Objects.Count} 个对象，FPS: {Fps:F1}";
        });
    }

    private void OnVideoInferenceCompleted(object? sender, EventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsVideoPlaying = false;
            Status = "视频推理完成";
            _detectionService.VideoFrameDetected -= OnVideoFrameDetected;
            _detectionService.VideoInferenceCompleted -= OnVideoInferenceCompleted;
        });
    }

    #endregion

    #region 实时相机检测

    /// <summary>
    /// 启动实时相机检测
    /// </summary>
    [RelayCommand]
    public void StartRealTimeDetection()
    {
        try
        {
            if (!IsModelLoaded)
            {
                MessageBox.Show("请先加载模型", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_cameraManager.IsConnected)
            {
                MessageBox.Show("请先连接相机", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_cameraManager.IsGrabbing)
            {
                MessageBox.Show("请先开始相机采集", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _framePipeline.StartInference();
            IsRealTimeDetecting = true;
            _inferenceFrameCount = 0;
            _lastInferenceTime = DateTime.Now;
            Status = "实时检测已启动";
        }
        catch (Exception ex)
        {
            Status = $"启动实时检测失败: {ex.Message}";
            MessageBox.Show($"启动实时检测失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 停止实时相机检测
    /// </summary>
    [RelayCommand]
    public void StopRealTimeDetection()
    {
        try
        {
            IsRealTimeDetecting = false;
            _realtimeSmoother.Clear();
            _lastRealtimeObjects = null;
            _noRealtimeResultFrameCount = 0;
            InferenceFps = 0;
            Status = "实时检测已停止";
        }
        catch (Exception ex)
        {
            Status = $"停止实时检测失败: {ex.Message}";
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// 释放资源 - 安全关闭相机和清理资源
    /// </summary>
    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            // MainWindow 在后台线程调用 Dispose，等待完整启动流程后再释放其创建的服务。
            _sopStartTask?.GetAwaiter().GetResult();

            // 取消订阅相机/检测/SOP 事件，避免关闭过程中收到回调
            _cameraManager.SlotStatusChanged -= OnCameraSlotStatusChanged;
            _cameraManager.ConnectionStatusChanged -= OnCameraConnectionStatusChanged;
            _detectionService.DetectionError -= OnDetectionError;
            if (_sopModule != null)
            {
                _sopModule.StepChanged -= OnSOPStepChanged;
                _sopModule.ViolationDetected -= OnSOPViolationDetected;
                _sopModule.WorkflowCompleted -= OnSOPWorkflowCompleted;
                _sopModule.StepCompleted -= OnSOPStepCompleted;
                _sopModule.ModelWarning -= OnSOPModelWarning;
                _sopModule.UseGpuChanged -= OnSopUseGpuChanged;
                _sopModule.GpuSwitchFailed -= OnSopGpuSwitchFailed;
            }

            _framePipeline.Dispose();
            SafeShutdownCamera();
            _audioFeedback.Dispose();

            // 释放 SOP 模块（内部 StopWorkflow 并释放 _yolo / 手部服务），此时推理线程已停止
            if (_sopModule != null)
            {
                try { _sopModule.Dispose(); } catch (Exception) { }
                _sopModule = null;
            }

            // 释放图像资源（可能仍被 ROI 编辑画面引用：先解除引用再释放）
            void DisposeImages()
            {
                ReleaseSharedImage(CurrentImage);
                ReleaseSharedImage(DetectionResultImage);
                foreach (var item in CameraViews)
                {
                    ReleaseSharedImage(item.CurrentImage);
                }
                CameraViews.Clear();
            }

            // CameraViews / SKBitmap 都必须在 UI 线程处理：
            // 后台线程 Clear() 会抛 NotSupportedException；在渲染期间释放位图则是原生崩溃。
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.Invoke(new Action(DisposeImages));
            else
                DisposeImages();

            // 释放检测服务（YOLO 推理会话）
            try { _detectionService?.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>
    /// 安全关闭相机 - 确保相机正确停止并断开连接
    /// </summary>
    private void SafeShutdownCamera()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("开始安全关闭相机...");

            // 使用CameraManager的Shutdown方法进行完整关闭
            _cameraManager.Shutdown();

            System.Diagnostics.Debug.WriteLine("相机已安全关闭");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"安全关闭相机时发生异常: {ex.Message}");
        }
    }

    #endregion
}

/// <summary>
/// 多相机画面项（主界面宫格中一个格子的数据源）
/// </summary>
public partial class CameraViewItem : ObservableObject
{
    public string CameraId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool IsPrimary { get; init; }

    /// <summary>该路相机最新画面（已叠加检测框，UI 线程写入）</summary>
    [ObservableProperty]
    private SKBitmap? _currentImage;

    [ObservableProperty]
    private bool _isConnected;

    /// <summary>跨相机规则命中时的告警高亮</summary>
    [ObservableProperty]
    private bool _isAlerting;

    /// <summary>
    /// 标记告警：红框高亮 seconds 秒后自动清除
    /// </summary>
    public void MarkAlert(int seconds)
    {
        IsAlerting = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            sw.Stop();
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => IsAlerting = false));
        });
    }
}
