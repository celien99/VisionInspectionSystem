using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VisionInspection.Core.Services;
using YoloDotNet;

namespace VisionInspection.Modules.Detection
{
    /// <summary>
    /// YOLO检测服务实现
    /// </summary>
    public class YoloDetectionService : IDetectionService
    {
        private Yolo? _yolo;
        private ModelInfo? _modelInfo;
        private readonly object _lockObject = new();
        private CancellationTokenSource? _videoInferenceCts;
        private Task? _videoTask;
        private VideoInferenceOptions? _videoOptions;
        private bool _isVideoProcessing = false;

        public bool IsInitialized => _yolo != null;

        public event EventHandler<DetectionResult>? DetectionCompleted;
        public event EventHandler<string>? DetectionError;
        public event EventHandler<VideoFrameResult>? VideoFrameDetected;
        public event EventHandler? VideoInferenceCompleted;

        public async Task<bool> InitializeAsync(ModelInfo modelInfo)
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (_lockObject)
                    {
                        // 如果正在视频推理，先停止
                        if (_isVideoProcessing)
                        {
                            StopVideoInference();
                            // 等待视频处理完全停止
                            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                            while (_isVideoProcessing && stopwatch.ElapsedMilliseconds < 5000)
                            {
                                Thread.Sleep(100);
                            }
                            // 额外等待确保资源释放
                            Thread.Sleep(500);
                        }

                        // 确保取消所有视频相关的回调
                        _yolo = null;
                        _videoOptions = null;
                        
                        // 强制GC回收，确保非托管资源释放
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();

                        _modelInfo = modelInfo;

                        // 诊断日志
                        System.Diagnostics.Debug.WriteLine($"[YoloDetectionService] ====== InitializeAsync ======");
                        System.Diagnostics.Debug.WriteLine($"[YoloDetectionService] 模型路径: {modelInfo.ModelPath}");
                        System.Diagnostics.Debug.WriteLine($"[YoloDetectionService] 文件存在: {File.Exists(modelInfo.ModelPath)}");

                        // 检查模型文件是否存在
                        if (!File.Exists(modelInfo.ModelPath))
                        {
                            System.Diagnostics.Debug.WriteLine($"[YoloDetectionService] 模型文件不存在!");
                            DetectionError?.Invoke(this, $"模型文件不存在: {modelInfo.ModelPath}");
                            return false;
                        }

                        // 创建YOLO实例
                        var options = YoloExecutionProviders.CreateOptions(
                            modelInfo.ModelPath, modelInfo.UseGpu, modelInfo.GpuId);

                        System.Diagnostics.Debug.WriteLine($"[YoloDetectionService] 执行设备: {(modelInfo.UseGpu ? $"CUDA (GPU {modelInfo.GpuId})" : "CPU")}");

                        _yolo = new Yolo(options);

                        // 更新模型信息中的类别列表
                        if (_yolo.OnnxModel?.Labels != null)
                        {
                            _modelInfo.Classes = _yolo.OnnxModel.Labels.Select(l => l.Name).ToList();
                            System.Diagnostics.Debug.WriteLine($"[YoloDetectionService] Yolo实例创建成功, 标签数: {_modelInfo.Classes.Count}");
                            if (_modelInfo.Classes.Count > 0)
                                System.Diagnostics.Debug.WriteLine($"[YoloDetectionService] 前5个标签: {string.Join(", ", _modelInfo.Classes.Take(5))}");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[YoloDetectionService] Yolo实例创建成功, 但无标签元数据");
                        }

                        return true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[YoloDetectionService] InitializeAsync 异常: {ex.GetType().Name}: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"[YoloDetectionService] 堆栈: {ex.StackTrace}");
                    DetectionError?.Invoke(this, $"初始化YOLO模型失败: {ex.Message}");
                    return false;
                }
            });
        }

        public void Dispose()
        {
            try { StopVideoInference(); } catch { /* 忽略停止异常 */ }
            var loopStopped = WaitVideoLoop(2000);

            lock (_lockObject)
            {
                _videoCapture?.Dispose();   // 已初始化但还没开始推理的解码器
                _videoCapture = null;

                // 解码线程若仍卡在 Read/检测里，不能释放它正在用的原生会话
                if (loopStopped)
                {
                    _yolo?.Dispose();
                    _yolo = null;
                }

                // _videoFrame 故意不释放：UI 可能仍在绘制它（释放后再绘制 = 原生崩溃），随进程回收
                _modelInfo = null;
            }

            DisposeVideoCts();
        }

        public async Task<DetectionResult> DetectAsync(SKBitmap image)
        {
            return await DetectAsync(image, new List<ROIInfo>());
        }

        public async Task<DetectionResult> DetectAsync(SKBitmap image, List<ROIInfo> rois)
        {
            if (_yolo == null || _modelInfo == null)
            {
                throw new InvalidOperationException("检测服务未初始化");
            }

            return await Task.Run(() =>
            {
                try
                {
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                    List<DetectedObject> detectedObjects = new();

                    // 根据模型类型执行不同的检测
                    switch (_modelInfo.Type)
                    {
                        case Core.Services.ModelType.ObjectDetection:
                            detectedObjects = RunObjectDetection(image, rois);
                            break;
                        case Core.Services.ModelType.Segmentation:
                            detectedObjects = RunSegmentation(image, rois);
                            break;
                        case Core.Services.ModelType.Classification:
                            detectedObjects = RunClassification(image);
                            break;
                        case Core.Services.ModelType.PoseEstimation:
                            detectedObjects = RunPoseEstimation(image, rois);
                            break;
                        default:
                            detectedObjects = RunObjectDetection(image, rois);
                            break;
                    }

                    stopwatch.Stop();

                    var result = new DetectionResult
                    {
                        Objects = detectedObjects,
                        ProcessingTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                        ImageWidth = image.Width,
                        ImageHeight = image.Height,
                        Timestamp = DateTime.Now
                    };

                    DetectionCompleted?.Invoke(this, result);
                    return result;
                }
                catch (Exception ex)
                {
                    DetectionError?.Invoke(this, $"检测失败: {ex.Message}");
                    throw;
                }
            });
        }

        private List<DetectedObject> RunObjectDetection(SKBitmap image, List<ROIInfo> rois)
        {
            var results = _yolo!.RunObjectDetection(
                image,
                confidence: _modelInfo!.ConfidenceThreshold,
                iou: _modelInfo.IouThreshold);

            var detectedObjects = results.Select(r => new DetectedObject
            {
                ClassId = r.Label.Index,
                ClassName = r.Label.Name,
                Confidence = (float)r.Confidence,
                BoundingBox = new[] { (float)r.BoundingBox.Left, (float)r.BoundingBox.Top, (float)r.BoundingBox.Width, (float)r.BoundingBox.Height },
                PixelBoundingBox = r.BoundingBox
            }).ToList();

            // 应用ROI过滤
            if (rois.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine($"应用ROI过滤前: {detectedObjects.Count} 个对象");
                ApplyROIFilter(detectedObjects, rois);
                System.Diagnostics.Debug.WriteLine($"应用ROI过滤后: {detectedObjects.Count} 个对象");
            }

            return detectedObjects;
        }

        private List<DetectedObject> RunSegmentation(SKBitmap image, List<ROIInfo> rois)
        {
            var results = _yolo!.RunSegmentation(
                image,
                confidence: _modelInfo!.ConfidenceThreshold,
                iou: _modelInfo.IouThreshold);

            var detectedObjects = results.Select(r => new DetectedObject
            {
                ClassId = r.Label.Index,
                ClassName = r.Label.Name,
                Confidence = (float)r.Confidence,
                BoundingBox = new[] { (float)r.BoundingBox.Left, (float)r.BoundingBox.Top, (float)r.BoundingBox.Width, (float)r.BoundingBox.Height },
                PixelBoundingBox = r.BoundingBox,
                Mask = r.BitPackedPixelMask,
                MaskBox = r.BoundingBox
            }).ToList();

            if (rois.Count > 0)
            {
                ApplyROIFilter(detectedObjects, rois);
            }

            return detectedObjects;
        }

        private List<DetectedObject> RunClassification(SKBitmap image)
        {
            var results = _yolo!.RunClassification(image);

            return results.Select(r => new DetectedObject
            {
                ClassId = 0,
                ClassName = r.Label,
                Confidence = (float)r.Confidence,
                BoundingBox = new[] { 0f, 0f, 1f, 1f },
                PixelBoundingBox = new SKRectI(0, 0, image.Width, image.Height)
            }).ToList();
        }

        private List<DetectedObject> RunPoseEstimation(SKBitmap image, List<ROIInfo> rois)
        {
            var results = _yolo!.RunPoseEstimation(
                image,
                confidence: _modelInfo!.ConfidenceThreshold,
                iou: _modelInfo.IouThreshold);

            var detectedObjects = results.Select(r => new DetectedObject
            {
                ClassId = r.Label.Index,
                ClassName = r.Label.Name,
                Confidence = (float)r.Confidence,
                BoundingBox = new[] { (float)r.BoundingBox.Left, (float)r.BoundingBox.Top, (float)r.BoundingBox.Width, (float)r.BoundingBox.Height },
                PixelBoundingBox = r.BoundingBox,
                KeyPoints = r.KeyPoints.Select((kp, idx) => new Core.Services.KeyPoint
                {
                    Index = idx,
                    X = kp.X,
                    Y = kp.Y,
                    Confidence = (float)kp.Confidence
                }).ToList()
            }).ToList();

            if (rois.Count > 0)
            {
                ApplyROIFilter(detectedObjects, rois);
            }

            return detectedObjects;
        }

        private void ApplyROIFilter(List<DetectedObject> objects, List<ROIInfo> rois)
        {
            // 过滤掉不在任何ROI内的对象
            objects.RemoveAll(obj =>
            {
                var objCenterX = obj.PixelBoundingBox.MidX;
                var objCenterY = obj.PixelBoundingBox.MidY;
                
                System.Diagnostics.Debug.WriteLine($"检查对象: {obj.ClassName} 中心点({objCenterX:F1}, {objCenterY:F1})");

                foreach (var roi in rois)
                {
                    System.Diagnostics.Debug.WriteLine($"  检查ROI: {roi.Name} 区域({roi.X}, {roi.Y}, {roi.Width}, {roi.Height})");
                    if (IsPointInROI(objCenterX, objCenterY, roi))
                    {
                        obj.IsInRoi = true;
                        obj.RoiId = roi.Id;
                        System.Diagnostics.Debug.WriteLine($"  -> 在ROI内，保留");
                        return false; // 保留此对象
                    }
                }
                System.Diagnostics.Debug.WriteLine($"  -> 不在任何ROI内，移除");
                return true; // 移除此对象
            });
        }

        private bool IsPointInROI(float x, float y, ROIInfo roi)
        {
            switch (roi.ShapeType)
            {
                case ShapeType.Rectangle:
                    return x >= roi.X && x <= roi.X + roi.Width &&
                           y >= roi.Y && y <= roi.Y + roi.Height;

                case ShapeType.Circle:
                    var centerX = roi.X + roi.Width / 2;
                    var centerY = roi.Y + roi.Height / 2;
                    var radius = Math.Min(roi.Width, roi.Height) / 2;
                    var distance = Math.Sqrt(Math.Pow(x - centerX, 2) + Math.Pow(y - centerY, 2));
                    return distance <= radius;

                default:
                    return false;
            }
        }

        public void SetConfidenceThreshold(float threshold)
        {
            if (_modelInfo != null)
            {
                _modelInfo.ConfidenceThreshold = Math.Clamp(threshold, 0.01f, 1.0f);
            }
        }

        public void SetIouThreshold(float threshold)
        {
            if (_modelInfo != null)
            {
                _modelInfo.IouThreshold = Math.Clamp(threshold, 0.01f, 1.0f);
            }
        }

        public List<string> GetClasses()
        {
            return _modelInfo?.Classes ?? new List<string>();
        }

        #region 视频推理

        // 视频解码改用 OpenCvSharp（程序自带，无外部依赖），不再走 YoloDotNet 的 FFmpeg 视频管线。
        // 原因：YoloDotNet 用 `ffprobe -version` + 2 秒超时校验外部 ffprobe，冷启动（杀软扫描 / 网络盘 /
        // 慢盘）经常超时，报 "Failed to verify 'ffprobe': Process must exit before requested information
        // can be determined"。检测仍然走同一个 _yolo，只是取帧换成 OpenCV。
        private OpenCvSharp.VideoCapture? _videoCapture;
        private SKBitmap? _videoFrame;          // 复用的帧缓冲（UI 也会引用它绘制，不主动释放）
        private int _videoFrameInterval = 1;
        private long _videoMaxFrames;           // 0 = 处理到结尾

        public bool InitializeVideoInference(VideoInferenceOptions options)
        {
            if (_yolo == null || _modelInfo == null)
            {
                DetectionError?.Invoke(this, "检测服务未初始化");
                return false;
            }

            if (options == null || string.IsNullOrWhiteSpace(options.VideoPath))
            {
                DetectionError?.Invoke(this, "视频路径为空");
                return false;
            }

            if (!File.Exists(options.VideoPath))
            {
                DetectionError?.Invoke(this, $"视频文件不存在: {options.VideoPath}");
                return false;
            }

            // 取消上一段视频（不阻塞：旧解码线程检测到取消后自行退出并释放自己的解码器）
            StopVideoInference();

            try
            {
                var capture = new OpenCvSharp.VideoCapture(options.VideoPath);
                if (!capture.IsOpened())
                {
                    capture.Dispose();
                    DetectionError?.Invoke(this, $"无法解码视频文件（编码不受 OpenCV 支持？）: {options.VideoPath}");
                    return false;
                }

                _videoOptions = options;
                _videoFrameInterval = Math.Max(1, options.FrameInterval); // 0 与 1 都表示每帧

                var fps = capture.Get(OpenCvSharp.VideoCaptureProperties.Fps);
                if (double.IsNaN(fps) || fps <= 0) fps = 25;

                if (options.StartTimeSeconds > 0)
                    capture.Set(OpenCvSharp.VideoCaptureProperties.PosFrames, options.StartTimeSeconds * fps);

                _videoMaxFrames = options.DurationSeconds > 0
                    ? (long)Math.Round(options.DurationSeconds * fps)
                    : 0;

                _videoCapture?.Dispose();   // 上一次初始化但没开始推理的解码器
                _videoCapture = capture;
                return true;
            }
            catch (Exception ex)
            {
                DetectionError?.Invoke(this, $"初始化视频推理失败: {ex.Message}");
                return false;
            }
        }

        public void StartVideoInference()
        {
            if (_yolo == null || _videoOptions == null || _videoCapture == null)
            {
                DetectionError?.Invoke(this, "视频推理未初始化");
                return;
            }

            // 解码器交给解码线程独占：停止/重新初始化时不碰它，避免"释放后仍在 Read"的原生崩溃
            var capture = _videoCapture;
            _videoCapture = null;

            DisposeVideoCts();
            var cts = new CancellationTokenSource();
            _videoInferenceCts = cts;
            _isVideoProcessing = true;

            _videoTask = Task.Run(() => VideoLoop(capture, cts.Token), cts.Token);
        }

        public void StopVideoInference()
        {
            _isVideoProcessing = false;

            try { _videoInferenceCts?.Cancel(); }
            catch (Exception ex) { DetectionError?.Invoke(this, $"停止视频推理时出错: {ex.Message}"); }

            // 不在这里阻塞等待：停止后 _isVideoProcessing=false，帧回调不会再生效；
            // 解码器由解码线程自己释放。需要确认线程退出的地方调用 WaitVideoLoop。
        }

        /// <summary>等待解码线程退出（最多 ms 毫秒），返回是否已退出。</summary>
        private bool WaitVideoLoop(int ms)
        {
            var task = _videoTask;
            if (task == null) return true;

            try
            {
                if (!task.Wait(ms)) return false;
            }
            catch { /* 异常已在解码线程内记录 */ }

            _videoTask = null;
            return true;
        }

        private void DisposeVideoCts()
        {
            try { _videoInferenceCts?.Dispose(); } catch { }
            _videoInferenceCts = null;
        }

        /// <summary>解码 + 逐帧检测（后台线程）。capture 由本线程独占并负责释放。</summary>
        private void VideoLoop(OpenCvSharp.VideoCapture capture, CancellationToken token)
        {
            long frameIndex = 0;
            long processed = 0;

            try
            {
                using var frame = new OpenCvSharp.Mat();

                while (!token.IsCancellationRequested && _isVideoProcessing)
                {
                    if (!capture.Read(frame) || frame.Empty())
                        break;   // 读到结尾

                    // FrameInterval：每 N 帧处理一帧（1 = 每帧）
                    if (_videoFrameInterval > 1 && frameIndex % _videoFrameInterval != 0)
                    {
                        frameIndex++;
                        continue;
                    }

                    _videoFrame = VideoFrameConverter.ToSKBitmap(frame, _videoFrame);
                    OnVideoFrameReceived(_videoFrame, frameIndex, token);

                    frameIndex++;
                    processed++;

                    if (_videoMaxFrames > 0 && processed >= _videoMaxFrames)
                        break;
                }
            }
            catch (Exception ex)
            {
                DetectionError?.Invoke(this, $"视频推理失败: {ex.Message}");
            }
            finally
            {
                _isVideoProcessing = false;
                capture.Dispose();
            }

            // 只有自然播完才通知"完成"；用户手动停止不通知
            if (!token.IsCancellationRequested)
                VideoInferenceCompleted?.Invoke(this, EventArgs.Empty);
        }

        private void OnVideoFrameReceived(SKBitmap frame, long frameIndex, CancellationToken token)
        {
            // 如果正在停止或已停止，不再处理帧（token 检查防止"上一段视频"取消后还继续跑检测）
            if (token.IsCancellationRequested || !_isVideoProcessing || _yolo == null || _modelInfo == null) return;

            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // 执行检测
                List<DetectedObject> detectedObjects = _modelInfo.Type switch
                {
                    Core.Services.ModelType.ObjectDetection => RunObjectDetection(frame, new List<ROIInfo>()),
                    Core.Services.ModelType.Segmentation => RunSegmentation(frame, new List<ROIInfo>()),
                    Core.Services.ModelType.Classification => RunClassification(frame),
                    Core.Services.ModelType.PoseEstimation => RunPoseEstimation(frame, new List<ROIInfo>()),
                    _ => RunObjectDetection(frame, new List<ROIInfo>())
                };

                stopwatch.Stop();

                var result = new DetectionResult
                {
                    Objects = detectedObjects,
                    ProcessingTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                    ImageWidth = frame.Width,
                    ImageHeight = frame.Height,
                    Timestamp = DateTime.Now
                };

                var frameResult = new VideoFrameResult
                {
                    FrameIndex = frameIndex,
                    Frame = frame,
                    DetectionResult = result
                };

                VideoFrameDetected?.Invoke(this, frameResult);
                DetectionCompleted?.Invoke(this, result);
            }
            catch (Exception ex)
            {
                DetectionError?.Invoke(this, $"处理帧 {frameIndex} 失败: {ex.Message}");
            }
        }

        #endregion
    }
}
