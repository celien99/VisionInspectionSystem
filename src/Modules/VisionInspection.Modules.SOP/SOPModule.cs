using Microsoft.Extensions.Configuration;
using SkiaSharp;
using System.Diagnostics;
using VisionInspection.Core.Interfaces;
using VisionInspection.Core.Models;
using VisionInspection.Core.Services;
using VisionInspection.Modules.Detection;
using VisionInspection.Modules.SOP.Models;
using VisionInspection.Modules.SOP.Services;

namespace VisionInspection.Modules.SOP;

/// <summary>
/// SOP 检测模块：管理物体检测、手部服务和工作流执行。
/// </summary>
public class SOPModule : IDetectionModule
{
    private SOPModuleConfig _config = new();
    private YoloModel? _yolo;
    private SOPStateMachine? _stateMachine;
    private SOPWorkflow? _currentWorkflow;
    private bool _x1TriggerSubscribed;
    private readonly object _lockObject = new();
    private readonly CrossCameraEvaluator _crossEvaluator = new();
    private readonly RegionMonitoringEvaluator _regionMonitoring = new();

    // 模型动态加载相关
    private string _lastLoadedModelPath = "";

    // ⭐ 步骤级多模型缓存：key = 模型路径（YAML step.model），value = 模型适配器。
    // 支持给每个相机/步骤指定不同模型，按需懒加载并缓存复用。
    private readonly Dictionary<string, YoloModel> _modelCache = new(StringComparer.OrdinalIgnoreCase);

    // 手部姿态估计相关
    private IHandPoseEstimationService? _handPoseService;

    // 手部服务初始化失败后的重试退避：原实现每帧都重试一次（模型缺失时会反复做
    // 文件存在性检查 + 写日志），是"检测一开就卡"的一个来源。
    private DateTime _lastHandInitAttempt = DateTime.MinValue;
    private const int HandInitRetrySeconds = 10;

    // SOP 运行数据记录器：每个周期(OK/NG)+每步耗时写入按天文件
    private SopRunRecorder? _runRecorder;

    // ★ 黄金数据集旁路录制器（P0）：默认 null（不录制），StartGoldenRecording 时才创建
    private GoldenRecorder? _goldenRecorder;

    // 当前操作人（登录用户名），由上层（MainViewModel/MainWindow）在登录后写入，
    // 在每次 WorkflowCompleted 触发时注入事件参数，供记录器落盘与看板按用户过滤。
    private string _currentOperator = "";

    // 文件日志记录器
    private static readonly object _sopLogLock = new();
    private static readonly string _sopDebugLogPath = AppPaths.Log("sop_module_debug.log");

    private void DebugLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var logLine = $"[{timestamp}] [SOPModule] {message}";

        Console.WriteLine(logLine);
        Debug.WriteLine(logLine);

        // 逐帧日志限流：每秒最多 10 行写文件（Console/Debug 不限），
        // 避免每帧多次 open/write/close 拖慢相机回调与推理线程
        if (LogThrottle.Allow("sop_module", 10))
        {
            lock (_sopLogLock)
            {
                try
                {
                    File.AppendAllText(_sopDebugLogPath, logLine + Environment.NewLine);
                }
                catch { }
            }
        }
    }

    public string Name => "SOPModule";
    public ModuleState State { get; private set; } = ModuleState.Uninitialized;

    /// <summary>物体检测模型是否已成功加载（false = 找不到/加载失败，检测将永远返回 0 个目标）。</summary>
    public bool IsModelLoaded => _yolo != null;
    public SOPStateMachine? StateMachine => _stateMachine;
    public SOPWorkflow? CurrentWorkflow => _currentWorkflow;

    /// <summary>
    /// 当前操作人（登录用户名）。上层在用户登录后写入，每次运行周期完成时由模块注入到
    /// WorkflowCompleted 事件参数，供 SOP 运行记录器落盘、数据看板按用户过滤。
    /// </summary>
    public string CurrentOperator
    {
        get => _currentOperator;
        set => _currentOperator = value ?? "";
    }

    /// <summary>
    /// 统一日志输出方法（同时输出到 Console、Debug 和文件）
    /// </summary>
    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var logLine = $"[{timestamp}] [SOP] {message}";

        // 1. 输出到 Console
        Console.WriteLine(logLine);

        // 2. 输出到 Debug（可在 Visual Studio 输出窗口查看）
        Debug.WriteLine(logLine);

        // 3. 写入文件（确保不丢失）
        try
        {
            File.AppendAllText(_sopDebugLogPath, logLine + Environment.NewLine);
        }
        catch
        {
            // 忽略文件写入错误
        }
    }

    public event EventHandler<StepChangedEventArgs>? StepChanged;
    public event EventHandler<StepCompletedEventArgs>? StepCompleted;
    public event EventHandler<ViolationEventArgs>? ViolationDetected;
    public event EventHandler<SOPCompletedEventArgs>? WorkflowCompleted;

    /// <summary>
    /// 模型加载告警（例如工作流指定的专用模型文件不存在、回退到通用COCO模型时触发）。
    /// 用于把"静默回退"暴露给界面，避免操作员面对"什么都没识别到"却无任何提示。
    /// </summary>
    public event EventHandler<string>? ModelWarning;

    /// <summary>
    /// 主检测计算设备变化通知（true=GPU/CUDA，false=CPU）。
    /// 用于界面切换开关与实际状态保持同步（例如 CUDA 不可用时自动回退 CPU）。
    /// </summary>
    public event EventHandler<bool>? UseGpuChanged;

    /// <summary>
    /// 尝试切换到 GPU 但 CUDA 不可用、已回退 CPU 时触发，供界面给出告警。
    /// </summary>
    public event EventHandler? GpuSwitchFailed;

    /// <summary>
    /// 当前主检测是否运行在 GPU 上。
    /// </summary>
    public bool IsUsingGpu => _config?.UseGpu ?? false;

    /// <summary>
    /// 运行时切换主检测的计算设备（GPU/CPU）。切换会释放并重建 YOLO 推理会话，立即生效。
    /// 返回切换后的最终状态（CUDA 不可用时自动回退 CPU 并返回 false）。
    /// </summary>
    public Task<bool> SetUseGpuAsync(bool useGpu)
        => Task.Run(() =>
    {
        lock (_lockObject)
        {
            if (_config == null) return false;

            if (!YoloExecutionProviders.IsCudaBuild)
            {
                // CPU 构建没有 CUDA 提供程序：保持 CPU，并让 UI 知道切换没生效
                _config.UseGpu = false;
                if (useGpu) GpuSwitchFailed?.Invoke(this, EventArgs.Empty);
                UseGpuChanged?.Invoke(this, false);
                return false;
            }

            // 无变化且模型已加载则不重建
            if (_yolo != null && _config.UseGpu == useGpu)
                return _config.UseGpu;

            // 释放旧会话并清空缓存，绕过 LoadYolo 内部"路径未变跳过重建"逻辑
            DisposeAllModels();
            _lastLoadedModelPath = "";

            _config.UseGpu = useGpu;
            LoadYolo(_config.ModelPath, useGpu, _config.GpuId.ToString());

            if (_yolo == null)
            {
                // 目标设备加载失败（最常见：CUDA 不可用）。回退 CPU 保证功能可用。
                _config.UseGpu = false;
                _lastLoadedModelPath = "";
                LoadYolo(_config.ModelPath, false, _config.GpuId.ToString());
                GpuSwitchFailed?.Invoke(this, EventArgs.Empty);
            }

            UseGpuChanged?.Invoke(this, _config.UseGpu);
            return _config.UseGpu;
        }
    });

    public Task InitializeAsync(IConfiguration config)
        => Task.Run(() =>
    {
        lock (_lockObject)
        {
            State = ModuleState.Initializing;

            try
            {
                _config = SOPModuleConfig.FromConfiguration(config);

                if (!YoloExecutionProviders.IsCudaBuild && _config.UseGpu)
                {
                    _config.UseGpu = false;
                    Log("当前为 CPU 构建（如需 CUDA：dotnet build -p:YoloEp=Cuda），已强制 UseGpu=false");
                }

                Console.WriteLine($"[SOP] 配置加载完成: ModelPath={_config.ModelPath}, UseGpu={_config.UseGpu}");

                // 初始化物体检测
                InitializeYolo();

                // 初始化状态机（先初始化状态机，确保核心功能可用）
                _stateMachine = new SOPStateMachine();
                SubscribeToStateMachineEvents();

                // 启用 SOP 运行数据记录器：每个周期(OK/NG) + 每步耗时写入按天文件 sop_records/
                _runRecorder = new SopRunRecorder();
                _runRecorder.Subscribe(this);
                Log("SOP 运行数据记录器已启用，记录写入 sop_records/（每个周期一行 jsonl + 每步一行 csv）");

                State = ModuleState.Ready;

                // 手部姿态估计服务改为延迟初始化，在第一次需要时初始化
                // 避免启动时卡住
                Log("手部姿态估计服务将在需要时延迟初始化");
            }
            catch (KeyNotFoundException knfEx)
            {
                State = ModuleState.Error;
                Console.WriteLine($"[SOP ERROR] KeyNotFoundException: {knfEx.Message}");
                Console.WriteLine($"[SOP ERROR] StackTrace: {knfEx.StackTrace}");
                throw new Exception($"SOP模式块初始化失?- 键未找到: {knfEx.Message}. 堆栈: {knfEx.StackTrace}", knfEx);
            }
            catch (Exception ex)
            {
                State = ModuleState.Error;
                Console.WriteLine($"[SOP ERROR] Exception: {ex.Message}");
                Console.WriteLine($"[SOP ERROR] StackTrace: {ex.StackTrace}");
                throw new Exception($"SOP模式块初始化失? {ex.Message}", ex);
            }
        }
    });

    /// <summary>
    /// 初始化 YOLO（启动时调用一次）
    /// </summary>
    private void InitializeYolo()
    {
        LoadYolo(_config.ModelPath, _config.UseGpu, _config.GpuId.ToString());

        // 启动时 CUDA 不可用（未安装 / 版本不匹配，例如只装了 CUDA 13 而 ORT 1.23 需要 CUDA 12）
        // → 自动回退 CPU。否则整个模块会没有模型、界面却显示"初始化完成"，表现为静默零检出。
        if (_yolo == null && _config.UseGpu)
        {
            Log("CUDA 不可用：GPU 模型加载失败，启动时自动回退 CPU（原因见上方模型加载失败日志）");
            _config.UseGpu = false;
            _lastLoadedModelPath = "";
            LoadYolo(_config.ModelPath, false, _config.GpuId.ToString());
            GpuSwitchFailed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 按指定路径加载?切换 YOLO 模式型
    /// </summary>
    private void LoadYolo(string modelPath, bool useGpu, string? gpuDevice = null)
    {
        Console.WriteLine($"[SOP LoadYolo] 开始加载模型: modelPath={modelPath}, useGpu={useGpu}");

        // 如果路径没变且模型已加载，跳过
        if (_yolo != null && modelPath == _lastLoadedModelPath)
        {
            Console.WriteLine($"[SOP] 模型未变化，跳过重载: {modelPath}");
            return;
        }

        try
        {
            // 释放旧模型
            _yolo?.Dispose();
            _yolo = null;

            // Resolve the model path by searching multiple possible locations
            var resolvedPath = ResolveModelPath(modelPath);
            if (resolvedPath == null)
            {
                Console.WriteLine($"[SOP] 警告: 模型文件不存在: {modelPath}");
                _lastLoadedModelPath = "";
                return;
            }

            Console.WriteLine($"[SOP] 加载模型: {resolvedPath}");
            Console.WriteLine($"[SOP] 模型文件: {Path.GetFileName(resolvedPath)}");

            var gpuId = int.TryParse(gpuDevice, out var deviceId) ? deviceId : (_config?.GpuId ?? 0);
            _yolo = YoloModel.Load(resolvedPath, useGpu, gpuId, out var loadError);
            if (_yolo == null)
            {
                Console.WriteLine($"[SOP] 模型加载失败: {loadError}");
                _lastLoadedModelPath = "";
                return;
            }

            _lastLoadedModelPath = modelPath;
            Console.WriteLine($"[SOP] 模型加载成功: {Path.GetFileName(resolvedPath)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SOP] 模型加载失败: {ex.Message}");
            _yolo = null;
            _lastLoadedModelPath = "";
        }
    }

    /// <summary>
    /// 相对路径 → 绝对路径。查找顺序统一由 Core 的 <see cref="AppPaths.ResolveModel"/> 负责
    /// （程序根 → 根下 models/、yolo_models/ → 向上 4 层 → 工作目录），不再各写一套。
    /// </summary>
    private static string? ResolveModelPath(string modelPath) => AppPaths.ResolveModel(modelPath);

    /// <summary>
    /// 初始化手部姿态估计服务
    /// </summary>
    private void InitializeHandPoseEstimation()
    {
        if (!_config.HandPoseEstimation.Enabled || _handPoseService?.IsInitialized == true)
            return;

        try
        {
            var rawModelPath = _config.HandPoseEstimation.ModelPath;

            Log($"开始初始化手部姿态估计，原始路径: '{rawModelPath}'");

            // 如果路径为空，尝试使用默认目录
            if (string.IsNullOrEmpty(rawModelPath))
            {
                rawModelPath = "models";
                Log($"路径为空，使用默认目录: {rawModelPath}");
            }

            var resolvedModelDir = ResolveModelPath(rawModelPath);
            var actualModelPath = resolvedModelDir ?? rawModelPath;

            Log($"解析后路径: {actualModelPath}, 是否目录: {Directory.Exists(actualModelPath)}, 是否文件: {File.Exists(actualModelPath)}");

            var handConfig = _config.HandPoseEstimation.Copy();
            handConfig.ModelPath = actualModelPath;

            Log($"PalmModelPath: '{handConfig.PalmModelPath}', LandmarkModelPath: '{handConfig.LandmarkModelPath}'");
            Log($"Palm模型存在: {File.Exists(handConfig.PalmModelPath)}, Landmark模型存在: {File.Exists(handConfig.LandmarkModelPath)}");

            ShutdownHandPoseService();

            // 选择手部检测方案
            // Backend=MediaPipe / YoloPose : 按配置强制使用该方案
            // Backend=Auto（默认）          : MediaPipe → YOLO-pose 兜底
            // MediaPipe 为默认后端（握拳/横向手泛化好、无需额外大模型）。
            // YoloPose 使用 yolov8s-pose 检测手腕 + MediaPipe Landmark 精修手指，
            // 对横伸/远离躯干的手识别率通常更高，作为兜底。

            if (handConfig.Backend == HandDetectionBackend.MediaPipe)
            {
                if (File.Exists(handConfig.PalmModelPath) && handConfig.PalmModelPath.Contains("palm_detection") && File.Exists(handConfig.LandmarkModelPath))
                {
                    _handPoseService = new MediaPipeHandService();
                    _handPoseService.InitializeAsync(handConfig).GetAwaiter().GetResult();
                    Log($"MediaPipe 手部姿态初始化完成, IsInitialized={_handPoseService.IsInitialized}");
                }
                else
                {
                    Log("Backend=MediaPipe 但模型文件缺失，手部检测未初始化");
                }
            }
            else if (handConfig.Backend == HandDetectionBackend.YoloPose)
            {
                TryInitializeYoloPoseHand(handConfig, "YoloPose");
            }
            else // Auto：MediaPipe 优先，失败兜底 YOLO-pose
            {
                bool useMediaPipe = File.Exists(handConfig.PalmModelPath)
                    && handConfig.PalmModelPath.Contains("palm_detection")
                    && File.Exists(handConfig.LandmarkModelPath);

                if (useMediaPipe)
                {
                    Log($"检测到 MediaPipe 模型，使用 MediaPipe 方案（默认后端，握拳/横向手支持更好）");
                    _handPoseService = new MediaPipeHandService();
                    _handPoseService.InitializeAsync(handConfig).GetAwaiter().GetResult();
                    Log($"MediaPipe 手部姿态初始化完成, IsInitialized={_handPoseService.IsInitialized}");
                }
                else
                {
                    TryInitializeYoloPoseHand(handConfig, "Auto 兜底");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"手部姿态估计初始化失败: {ex.Message}");
            if (ex.InnerException != null)
            {
                Log($"内部异常: {ex.InnerException.Message}");
            }
            ShutdownHandPoseService();
        }
    }

    /// <summary>
    /// YOLO-pose 手部后端：pose 检测手腕 + MediaPipe landmark 精修手指。
    /// 模型位置按「配置中显式指定的 .onnx → AppPaths 标准目录（models/、yolo_models/）」解析，
    /// 找不到就降级（只记日志，不影响 SOP 其它功能）。
    /// </summary>
    private void TryInitializeYoloPoseHand(HandPoseEstimationConfig handConfig, string tag)
    {
        // 配置里如果直接给了 pose 模型文件，优先用它（之前这段被 e:\ 硬编码完全绕过）
        var configuredPose = handConfig.PalmModelPath;
        var poseModel = File.Exists(configuredPose) && configuredPose.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)
            ? configuredPose
            : AppPaths.ResolveModel("yolov8s-pose.onnx") ?? AppPaths.ResolveModel("yolov11s-pose.onnx");

        var landmarkModel = AppPaths.ResolveModel("hand_landmark_sparse_Nx3x224x224.onnx")
                            ?? (File.Exists(handConfig.LandmarkModelPath) ? handConfig.LandmarkModelPath : null);

        if (poseModel == null || landmarkModel == null)
        {
            Log($"{tag}: YOLO-pose 模型缺失，手部检测未初始化（pose={poseModel ?? "缺"}, landmark={landmarkModel ?? "缺"}）");
            return;
        }

        handConfig.PalmModelPath = poseModel;
        handConfig.LandmarkModelPath = landmarkModel;
        _handPoseService = new YoloPoseHandEstimationService();
        _handPoseService.InitializeAsync(handConfig).GetAwaiter().GetResult();
        Log($"{tag}: 手部姿态初始化成功, pose={poseModel}, landmark={landmarkModel}");
    }

    private void SubscribeToStateMachineEvents()
    {
        if (_stateMachine == null) return;

        _stateMachine.StepChanged += (s, e) =>
        {
            StepChanged?.Invoke(this, e);
        };

        // ⭐ 转发每步完成事件给 UI 层（逐步骤缩略图刷新等），保留内部 Modbus 触发订阅不变
        _stateMachine.StepCompleted += (s, e) =>
        {
            StepCompleted?.Invoke(this, e);
        };

        _stateMachine.ViolationDetected += (s, e) =>
        {
            ViolationDetected?.Invoke(this, e);
        };

        _stateMachine.WorkflowCompleted += (s, e) =>
        {
            // 注入操作人（登录用户名），供记录器落盘与看板按用户过滤
            e.Operator = _currentOperator;
            WorkflowCompleted?.Invoke(this, e);
        };

        // ⭐ 步骤完成后若发生违规，触发 Modbus 功能码 06（写单个寄存器 Y1-Y8）
        _stateMachine.StepCompleted += (s, e) =>
        {
            if (e.HasViolations)
                FireModbusTriggerForStep(e.Step);
        };

        // ⭐ X1（寄存器 0）上升沿作为 SOP 采集触发：空闲时启动已加载的工作流。
        // ModbusService 为单例，此处仅订阅一次，避免模块重复初始化时叠加处理函数。
        if (!_x1TriggerSubscribed)
        {
            _x1TriggerSubscribed = true;
            ModbusService.Instance.X1Trigger += OnX1Trigger;
        }
    }

    private void OnX1Trigger(object? sender, EventArgs e)
    {
        lock (_lockObject)
        {
            try
            {
                if (_stateMachine?.CurrentState == SOPExecutionState.Idle && _currentWorkflow != null)
                    StartWorkflow(_currentWorkflow);
            }
            catch (Exception ex)
            {
                Log($"[Modbus] X1 采集触发启动失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 步骤违规触发 Modbus 06 写单个寄存器（Y1-Y8）。
    /// </summary>
    private void FireModbusTriggerForStep(SOPStep step)
    {
        var cfg = step.ModbusTrigger;
        if (cfg == null || !cfg.Enabled) return;
        try
        {
            ModbusService.Instance.TriggerOutput(cfg.Address, cfg.PulseMs, cfg.Latch);
            var mode = cfg.Latch ? "锁存(需管理员复位)" : $"脉宽 {cfg.PulseMs}ms";
            Log($"[Modbus] 步骤「{step.StepName}」违规触发 06 → Y{cfg.Address + 1} ({mode})");
        }
        catch (Exception ex)
        {
            Log($"[Modbus] 违规触发失败: {ex.Message}");
        }
    }

    public Task<ModuleResult> ProcessAsync(Dictionary<string, CaptureFrame> frames)
    {
        lock (_lockObject)
        {
            var result = new SOPModuleResult { ModuleName = Name };
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                State = ModuleState.Processing;

                if (_stateMachine?.CurrentState != SOPExecutionState.Running)
                {
                    result.Success = true;
                    result.ErrorMessage = "SOP未在运行状态态";
                    result.Level = DefectLevel.Good;
                    return Task.FromResult<ModuleResult>(result);
                }

                if (frames.Count == 0)
                {
                    result.Success = false;
                    result.ErrorMessage = "无图像帧";
                    result.Level = DefectLevel.Critical;
                    return Task.FromResult<ModuleResult>(result);
                }

                var mainFrame = frames.FirstOrDefault(f => f.Key == "main_camera").Value;
                if (mainFrame == null)
                {
                    result.Success = false;
                    result.ErrorMessage = "未找到主相机帧";
                    result.Level = DefectLevel.Critical;
                    return Task.FromResult<ModuleResult>(result);
                }

                ProcessMultiCameraFrames(frames, result);
                if (_currentWorkflow is { } workflow)
                {
                    var now = DateTime.Now;
                    result.RegionAlert = _regionMonitoring.Evaluate(result.Detections, workflow, now);
                    if (workflow.CrossCamera is { } crossCamera)
                        result.CrossCameraAlert = _crossEvaluator.Evaluate(result.PerCameraDetections,
                            workflow.Regions, crossCamera, now);
                }

                stopwatch.Stop();
                result.ElapsedMs = stopwatch.ElapsedMilliseconds;
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.Level = DefectLevel.Critical;
            }
            finally
            {
                State = ModuleState.Ready;
            }

            return Task.FromResult<ModuleResult>(result);
        }
    }

    /// <summary>
    /// 多相机帧处理：在同一个锁内串行遍历各相机帧做 YOLO 推理。
    /// 支持步骤级 (相机, 模型)：每路相机可用自身绑定的模型（步骤中该相机首次指定，无则全局模型）；
    /// 状态机评估当前步骤时，使用该步骤指定相机的检测结果（不再固定主相机）。
    /// 注意：底层推理库复用实例级结果 List，并发调用会互相覆盖；
    /// 这里串行推理，且 YoloModel.Detect 内部立即拷贝成领域对象。
    /// </summary>
    private void ProcessMultiCameraFrames(Dictionary<string, CaptureFrame> frames, SOPModuleResult result)
    {
        var timestamp = DateTime.Now;

        // 0) 当前步骤 → 该步使用的 (相机, 模型)
        var step = GetCurrentStep();
        string stepCamera = string.IsNullOrWhiteSpace(step?.CameraId) ? "main_camera" : step.CameraId;
        string? stepModel = string.IsNullOrWhiteSpace(step?.ModelPath) ? null : step.ModelPath;

        // 1) 为每路相机确定"该相机使用的模型"（工作流中该相机首次指定；无则全局模型）
        var cameraModelMap = BuildCameraModelMap();

        // 2) 每路独立 YOLO 推理（串行，防底层结果 List 被覆盖）
        foreach (var (cameraId, frame) in frames)
        {
            var modelPath = cameraModelMap.TryGetValue(cameraId, out var camModel) ? camModel : null;
            var detections = RunObjectDetectionSafe(frame, modelPath);
            result.PerCameraDetections[cameraId] = detections;

            if (cameraId == "main_camera")
            {
                result.Detections = detections;
            }
        }

        // 3) 手部姿态 + 状态机推进：
        //    - 状态机检测结果严格取"当前步骤指定相机"的检测（不做跨相机 fallback，
        //      否则 cam_2 步骤会错误地拿主相机检测评估，导致无杯子也通过等误判）；
        //    - 手部检测同样使用当前步骤相机的画面，该相机无帧则不做手部。
        var mainFrame = frames.TryGetValue("main_camera", out var mf) ? mf : frames.Values.FirstOrDefault();

        List<DetectedObject> stateDetections;
        if (frames.ContainsKey(stepCamera))
        {
            // 步骤相机帧在：取该相机本轮检测（可能为空，但语义正确）
            stateDetections = result.PerCameraDetections.TryGetValue(stepCamera, out var stepDets)
                ? stepDets
                : new List<DetectedObject>();
        }
        else if (stepCamera == CameraManager.PrimaryCameraId && result.Detections != null)
        {
            // 主相机帧缺失时的兜底（兼容旧单相机路径）
            stateDetections = result.Detections;
        }
        else
        {
            // 步骤相机无帧：不通过
            stateDetections = new List<DetectedObject>();
        }

        var handFrame = frames.TryGetValue(stepCamera, out var hf) ? hf : mainFrame;
        var handResult = handFrame != null
            ? RunHandPoseDetection(handFrame, result, timestamp, stepCamera)
            : null;

        DebugLog($"步骤评估: step={step?.StepName}({stepCamera}) frames=[{string.Join(",", frames.Keys)}] perCam=[{string.Join(",", result.PerCameraDetections.Select(kv => $"{kv.Key}:{kv.Value.Count}"))}] stateDet={stateDetections.Count}");
        _stateMachine?.ProcessFrame(stateDetections, handResult, timestamp);

        // 4) 状态机结果回填
        UpdateStateMachineResult(result);

        // 5) ★ 黄金数据集旁路录制（P0）：在引擎判定之后复制数据，不影响任何判定分支
        TryRecordGoldenFrame(handFrame, stateDetections, handResult, timestamp);
    }

    /// <summary>
    /// 【黄金数据集旁路录制】把本帧"看到什么 + 判成什么"复制给录制器。
    ///
    /// 铁律：整段 try/catch 包裹，任何异常（含序列化失败、磁盘满）都只丢该帧，
    ///       绝不向检测流程抛出 —— 录制备录不能影响生产。
    /// </summary>
    private void TryRecordGoldenFrame(
        CaptureFrame? frame,
        List<DetectedObject> detections,
        HandPoseEstimationResult? handResult,
        DateTime timestamp)
    {
        var recorder = _goldenRecorder;
        if (recorder == null || !recorder.IsRecording) return;

        try
        {
            var sm = _stateMachine;
            if (sm == null) return;

            var step = GetCurrentStep();
            var snapshot = new SOPStateMachineSnapshot
            {
                Timestamp = timestamp,
                StepId = sm.CurrentStepId,
                StepName = step?.StepName ?? "",
                State = sm.CurrentState.ToString(),
                IsPass = sm.LastEvaluation?.IsPass ?? false,
                // NextSteps 是步骤 id 的字符串列表（YAML transitions）
                NextStepId = step != null && step.NextSteps.Count > 0
                    && int.TryParse(step.NextSteps[0], out var nid) ? nid : -1
            };

            // 逐条件布尔：CheckResults 与 PassConditions 同序
            var eval = sm.LastEvaluation;
            if (eval != null && step != null)
            {
                for (int i = 0; i < step.PassConditions.Count; i++)
                {
                    var key = step.PassConditions[i].Type.ToString();
                    // 评估器 AND 短路时可能只返回部分结果，未评估的记 false
                    bool met = i < eval.CheckResults.Count && eval.CheckResults[i].IsMet;
                    snapshot.Conditions[key] = met;
                }
            }

            // 本帧触发的违规（用于校验违规触发帧序号）
            foreach (var v in sm.Violations)
            {
                if ((timestamp - v.Timestamp).TotalMilliseconds < 200)
                    snapshot.Violations.Add(v.Type.ToString());
            }

            recorder.RecordFrame(frame, detections, handResult, snapshot);
        }
        catch (Exception ex)
        {
            // 绝不外泄
            DebugLog($"[Golden] 录制异常（已忽略）: {ex.Message}");
        }
    }

    /// <summary>
    /// 当前状态机所在步骤（用于步骤级相机/模型选择）
    /// </summary>
    private SOPStep? GetCurrentStep()
    {
        var id = _stateMachine?.CurrentStepId ?? 0;
        return _currentWorkflow?.Steps.FirstOrDefault(s => s.StepId == id);
    }

    /// <summary>
    /// 构建"相机 → 模型路径"映射：取工作流中每路相机首次被步骤指定的模型（无则全局）。
    /// </summary>
    private Dictionary<string, string?> BuildCameraModelMap()
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (_currentWorkflow == null) return map;

        foreach (var s in _currentWorkflow.Steps.OrderBy(s => s.Order))
        {
            var cam = string.IsNullOrWhiteSpace(s.CameraId) ? "main_camera" : s.CameraId;
            if (!map.ContainsKey(cam))
                map[cam] = string.IsNullOrWhiteSpace(s.ModelPath) ? null : s.ModelPath;
        }
        return map;
    }

    /// <summary>
    /// 单路 YOLO 物体检测。
    /// <para>
    /// 这里是推理层（YoloDotNet）与 SOP 领域层（<see cref="DetectedObject"/>）之间**唯一**的转换点：
    /// 引擎、条件评估器、违规检测器、UI 叠加都不再认识 YoloDotNet 的类型，
    /// 以后换 ONNX Runtime / RKNN 只改这里和各适配器。
    /// </para>
    /// </summary>
    private List<DetectedObject> RunObjectDetectionSafe(CaptureFrame frame, string? modelPath = null)
    {
        var detections = new List<DetectedObject>();
        var yolo = GetYoloForModel(modelPath);
        if (yolo == null)
        {
            DebugLog("YOLO 未初始化，跳过物体检测");
            return detections;
        }

        try
        {
            detections = yolo.Detect(frame.Image, _config.ConfidenceThreshold, _config.IouThreshold);
            DebugLog($"YOLO 物体检测 ({frame.CameraId}): {detections.Count} 个目标");
        }
        catch (Exception ex)
        {
            DebugLog($"YOLO 物体检测失败 ({frame.CameraId}): {ex.Message}");
        }
        return detections;
    }

    /// <summary>
    /// 获取指定模型路径对应的模型适配器：
    /// - 空/全局路径 → 返回全局 _yolo（由 InitializeAsync 加载）
    /// - 步骤路径 → 从 _modelCache 缓存取，未加载则懒加载（同一 _lockObject 保护加载与推理）
    /// </summary>
    private YoloModel? GetYoloForModel(string? modelPath)
    {
        var path = string.IsNullOrWhiteSpace(modelPath) ? _config.ModelPath : modelPath.Trim();
        if (string.Equals(path, _config.ModelPath, StringComparison.OrdinalIgnoreCase))
            return _yolo;

        lock (_lockObject)
        {
            if (_modelCache.TryGetValue(path, out var cached) && cached != null)
                return cached;

            var resolved = ResolveModelPath(path);
            if (resolved == null)
            {
                DebugLog($"步骤模型文件不存在: {path}");
                return null;
            }

            var yolo = YoloModel.Load(resolved, _config.UseGpu, _config.GpuId, out var loadError);
            if (yolo == null)
            {
                DebugLog($"步骤模型加载失败: {path}: {loadError}");
                return null;
            }

            _modelCache[path] = yolo;
            DebugLog($"步骤模型已加载: {Path.GetFileName(resolved)}");
            return yolo;
        }
    }

    /// <summary>
    /// 释放全部模型（全局 + 步骤缓存）
    /// </summary>
    private void DisposeAllModels()
    {
        try { _yolo?.Dispose(); } catch { /* 忽略 */ }
        _yolo = null;

        lock (_lockObject)
        {
            foreach (var kv in _modelCache)
            {
                try { kv.Value.Dispose(); } catch { /* 忽略 */ }
            }
            _modelCache.Clear();
        }
    }

    /// <summary>
    /// 指定相机画面的手部姿态检测（复用原 ProcessUnifiedDetection 的手部段）
    /// </summary>
    private HandPoseEstimationResult? RunHandPoseDetection(CaptureFrame frame, SOPModuleResult result, DateTime timestamp, string cameraId = "main_camera")
    {
        if (!_config.HandPoseEstimation.Enabled) return null;

        // 初始化失败后保留原有的 10 秒重试间隔。
        if (_handPoseService == null || !_handPoseService.IsInitialized)
        {
            if ((DateTime.Now - _lastHandInitAttempt).TotalSeconds < HandInitRetrySeconds)
                return null;

            _lastHandInitAttempt = DateTime.Now;
            try
            {
                InitializeHandPoseEstimation();
            }
            catch (Exception ex)
            {
                DebugLog($"初始化手部姿态估计服务失败: {ex.Message}");
            }
        }

        if (_handPoseService == null || !_handPoseService.IsInitialized)
            return null;

        try
        {
            var handResult = _handPoseService.DetectHandsAsync(frame.Image).GetAwaiter().GetResult();
            result.HandPoseResult = handResult;
            // ⭐ 记录手部结果来自哪路相机，供 UI 在该相机画面上绘制手部骨架
            result.HandPoseCameraId = cameraId;

            return handResult;
        }
        catch (Exception ex)
        {
            DebugLog($"手部姿态估计失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 状态机结果回填（CurrentStepId / Violations / StepResults 等）
    /// </summary>
    private void UpdateStateMachineResult(SOPModuleResult result)
    {
        if (_stateMachine == null) return;

        result.CurrentStepId = _stateMachine.CurrentStepId;
        result.CurrentState = _stateMachine.CurrentState;
        result.StepHistory = _stateMachine.StepHistory.ToList();
        result.Violations = _stateMachine.Violations.ToList();
        result.IsPass = !_stateMachine.Violations.Any();
        result.Level = result.IsPass ? DefectLevel.Good : DefectLevel.Critical;

        var currentStep = _currentWorkflow?.Steps.FirstOrDefault(s => s.StepId == _stateMachine.CurrentStepId);
        result.StepResults = new StepResults
        {
            Message = currentStep?.StepName ?? "等待开始",
            CurrentStep = _stateMachine.CurrentStepId,
            TotalSteps = _currentWorkflow?.Steps.Count ?? 0
        };
    }

    /// <summary>
    /// 统一检测模式处理（已拆分为 RunObjectDetectionSafe + RunHandPoseDetection，
    /// 见 ProcessMultiCameraFrames）
    /// </summary>
    public void StartWorkflow(SOPWorkflow workflow)
    {
        lock (_lockObject)
        {
            if (_stateMachine == null)
                throw new InvalidOperationException("状态机未初始化");

            _currentWorkflow = workflow;

            // 注入区域域定义（已有）
            if (workflow.Regions != null && workflow.Regions.Count > 0)
            {
                _stateMachine.UpdateZones(workflow.Regions);
                Log($"已加载载 {workflow.Regions.Count} 个区域域定义");
            }
            else
            {
                Log("警告: 工作作流中没有区域域定义，object_in_zone 条件将无法工作作");
            }

            // 检测查并切换 YOLO 模式型
            if (workflow.Model != null && !string.IsNullOrWhiteSpace(workflow.Model.Path))
            {
                var modelPath = workflow.Model.Path;

                // Resolve relative paths by searching common locations
                var resolvedPath = ResolveModelPath(modelPath);
                if (resolvedPath == null)
                {
                    var warn = $"工作流模型文件不存在: {modelPath}。\n" +
                               $"已回退到初始化时加载的通用(COCO)模型，该模型不含本产品专属类别" +
                               $"(phone/case_top/case_bottom/manual/charger/cable 等)，\n" +
                               $"因此物体检测、object_present / object_in_zone / hand_near_object / 漏放校验将全部失效。\n" +
                               $"请先训练专用模型并放到该路径，或修正 YAML 的 model.path。";
                    Log("警告: " + warn);
                    ModelWarning?.Invoke(this, warn);
                }
                else
                {
                    modelPath = resolvedPath;

                    if (modelPath != _lastLoadedModelPath)
                    {
                        Log($"切换模式型: {_lastLoadedModelPath} → {modelPath}");
                        Log($"模式型配置: confidence={workflow.Model.Confidence}, iou={workflow.Model.Iou}, gpu={workflow.Model.UseGpu}");

                        // 同步更新运行时置信度/IoU（本地生效，非配置文件）
                        _config.ConfidenceThreshold = workflow.Model.Confidence;
                        _config.IouThreshold = workflow.Model.Iou;
                        _config.GpuId = workflow.Model.GpuId;

                        LoadYolo(modelPath, workflow.Model.UseGpu, workflow.Model.GpuId.ToString());
                    }
                    else
                    {
                        Log($"模式型未变化，跳过: {Path.GetFileName(modelPath)}");
                    }
                }
            }

            // P0-3: 全局置信度接线 + 允许步骤 min_confidence 调低生效
            // 有效截断 = min(全局基准置信度, 所有步骤 min_confidence 的最小值)
            // 这样 settings.confidenceThreshold 与 step.min_confidence 双向都生效：
            //   - 全局调高 → 整体更严格；全局调低 → 整体更宽松
            //   - 步骤要求高于截断 → 步骤内 evaluator 用自身 min_confidence 二次过滤
            //   - 步骤要求低于截断（原"调低无效"问题）→ 因截断已降到该值，步骤可正常检出
            float baseConf;
            if (workflow.Settings != null && workflow.Settings.ConfidenceThreshold > 0)
                baseConf = workflow.Settings.ConfidenceThreshold;
            else if (workflow.Model != null)
                baseConf = workflow.Model.Confidence;
            else
                baseConf = _config.ConfidenceThreshold;

            float minStepConf = workflow.Steps
                .SelectMany(s => s.PassConditions)
                .Select(c => c.MinConfidence)
                .Where(v => v > 0)
                .DefaultIfEmpty(baseConf)
                .Min();
            _config.ConfidenceThreshold = Math.Min(baseConf, minStepConf);
            Log($"置信度设置: 基准={baseConf:F2} (settings/model), 步骤最低要求={minStepConf:F2} → YOLO截断={_config.ConfidenceThreshold:F2}");

            _stateMachine.Start(workflow);
        }
    }

    public void StopWorkflow()
    {
        lock (_lockObject)
        {
            _stateMachine?.Stop();
            _currentWorkflow = null;
            _regionMonitoring.Reset();
        }
    }

    public void ResetWorkflow()
    {
        lock (_lockObject)
        {
            if (_currentWorkflow != null)
            {
                _stateMachine?.Start(_currentWorkflow);
            }
        }
    }

    /// <summary>
    /// 从YAML文件加载载并启动工作作流
    /// </summary>
    public void StartWorkflowFromYaml(string yamlPath)
    {
        var workflow = SOPYamlConverter.LoadFromYaml(yamlPath);
        StartWorkflow(workflow);
    }

    public void Dispose()
    {
        lock (_lockObject)
        {
            if (_x1TriggerSubscribed)
            {
                ModbusService.Instance.X1Trigger -= OnX1Trigger;
                _x1TriggerSubscribed = false;
            }
            _runRecorder?.Unsubscribe(this);
            _runRecorder = null;
            StopWorkflow();
            _goldenRecorder?.Dispose();
            _goldenRecorder = null;
            ShutdownHandPoseService();
            DisposeAllModels();
        }
    }

    public Task ShutdownAsync() => Task.Run(Dispose);

    private void ShutdownHandPoseService()
    {
        _handPoseService?.ShutdownAsync().GetAwaiter().GetResult();
        _handPoseService = null;
    }

    public Task EnsureHandPoseServiceReadyAsync() => Task.Run(() =>
    {
        lock (_lockObject) InitializeHandPoseEstimation();
    });

    public Task UpdateHandPoseConfigAsync(HandPoseEstimationConfig config)
    {
        var snapshot = config.Copy();
        return Task.Run(() =>
        {
            lock (_lockObject)
            {
                var rebuild = _handPoseService != null;
                ShutdownHandPoseService();
                _config.HandPoseEstimation = snapshot;
                _lastHandInitAttempt = DateTime.MinValue;
                if (rebuild) InitializeHandPoseEstimation();
            }
        });
    }

    // ====================================================================
    // ★ 黄金数据集旁路录制（P0）—— 供 UI 控制，不影响任何判定逻辑
    // ====================================================================

    /// <summary>是否正在录制黄金数据集</summary>
    public bool IsGoldenRecording => _goldenRecorder?.IsRecording ?? false;

    /// <summary>录制状态一行摘要（供 UI 显示）</summary>
    public string GoldenStatus => _goldenRecorder?.StatusLine ?? "未录制";

    /// <summary>开始录制。失败返回 false，调用方仅提示，不打断检测。</summary>
    public bool StartGoldenRecording(GoldenRecorderConfig config)
    {
        try
        {
            // ★ 传入 zones 快照：随 meta.json 落盘，供 RK3588 复现基线时对齐区域坐标
            var zonesForRecord = _stateMachine?.Zones.Values.ToList();
            if (_goldenRecorder == null)
            {
                _goldenRecorder = new GoldenRecorder();
            }
            var ok = _goldenRecorder.Start(config, zonesForRecord);
            if (ok)
                GoldenRecorderConfigManager.Save(config);
            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Golden] 启动录制异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>停止录制并落盘 summary.json</summary>
    public void StopGoldenRecording()
    {
        try { _goldenRecorder?.Stop(); }
        catch (Exception ex) { Console.WriteLine($"[Golden] 停止录制异常: {ex.Message}"); }
    }

}

/// <summary>
/// SOP 模块配置
/// </summary>
public class SOPModuleConfig
{
    public string ModelPath { get; set; } = "yolo_models/yolov8s.onnx";
    public bool UseGpu { get; set; } = true;

    /// <summary>CUDA 设备序号（多卡机器用；来自配置或配方 model.gpu_id）。</summary>
    public int GpuId { get; set; } = 0;

    public float ConfidenceThreshold { get; set; } = 0.6f;
    public float IouThreshold { get; set; } = 0.45f;

    /// <summary>
    /// 手部姿态估计计配?    /// </summary>
    public HandPoseEstimationConfig HandPoseEstimation { get; set; } = new();

    public static SOPModuleConfig FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("SOPModule");
        var result = section.Get<SOPModuleConfig>() ?? new SOPModuleConfig();
        // 兼容已发布 JSON 的顶层后端字段；新参数直接由配置类型绑定。
        if (Enum.TryParse<HandDetectionBackend>(section["HandDetectionBackend"], true, out var backend))
            result.HandPoseEstimation.Backend = backend;
        return result;
    }
}

/// <summary>
/// SOP模式块结果果
/// </summary>
public class SOPModuleResult : ModuleResult
{
    public int CurrentStepId { get; set; }
    public SOPExecutionState CurrentState { get; set; }
    public List<StepExecutionRecord> StepHistory { get; set; } = new();
    public List<ViolationRecord> Violations { get; set; } = new();
    public string? RegionAlert { get; set; }
    public CrossCameraRuleHit? CrossCameraAlert { get; set; }
    public bool IsPass { get; set; }

    /// <summary>
    /// 检测测结果果（物体检测测模式式）——主相机结果（兼容旧逻辑）
    /// </summary>
    public List<DetectedObject> Detections { get; set; } = new();

    /// <summary>
    /// 每路相机的检测结果（key = frames 字典的 key，如 "main_camera" / "cam_2"）。
    /// 每路均为独立 ToList 拷贝，供跨相机协同规则（CrossCameraEvaluator）使用。
    /// </summary>
    public Dictionary<string, List<DetectedObject>> PerCameraDetections { get; set; } = new();

    /// <summary>
    /// 手部姿态检测结果
    /// </summary>
    public HandPoseEstimationResult? HandPoseResult { get; set; }

    /// <summary>
    /// 手部姿态结果来自哪路相机的画面（用于 UI 在该相机画面上绘制手部骨架）
    /// </summary>
    public string HandPoseCameraId { get; set; } = "main_camera";

    /// <summary>
    /// 步骤结果果信息（用于UI显示?    /// </summary>
    public StepResults StepResults { get; set; } = new();
}

/// <summary>
/// 步骤结果果显示信息
/// </summary>
public class StepResults
{
    /// <summary>
    /// 状态态消?    /// </summary>
    public string Message { get; set; } = "";

    /// <summary>
    /// 当前步骤
    /// </summary>
    public int CurrentStep { get; set; }

    /// <summary>
    /// 总步骤数
    /// </summary>
    public int TotalSteps { get; set; }
}

