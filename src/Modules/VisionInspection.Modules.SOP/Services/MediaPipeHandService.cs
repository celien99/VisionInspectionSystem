using SkiaSharp;
using VisionInspection.Modules.SOP.Models;

namespace VisionInspection.Modules.SOP.Services;

/// <summary>推理由 SOP 工作线程执行；检测与释放共用同一把锁。</summary>
public class MediaPipeHandService : IHandPoseEstimationService
{
    private MediaPipeHandDetector? _detector;
    private readonly object _lock = new();

    public bool IsInitialized => _detector?.IsInitialized ?? false;

    public Task InitializeAsync(HandPoseEstimationConfig config)
    {
        lock (_lock)
        {
            _detector = new MediaPipeHandDetector(config);
            _detector.Initialize();
            Console.WriteLine($"[MediaPipeHand] 初始化完成, IsInitialized={_detector.IsInitialized}, GPU={_detector.UsingGpu}");
        }
        return Task.CompletedTask;
    }

    public Task<HandPoseEstimationResult> DetectHandsAsync(SKBitmap image)
    {
        lock (_lock)
        {
            return Task.FromResult(new HandPoseEstimationResult
            {
                Hands = _detector?.IsInitialized == true ? _detector.DetectHands(image) : new(),
                Timestamp = DateTime.Now
            });
        }
    }

    public Task ShutdownAsync()
    {
        lock (_lock)
        {
            _detector?.Dispose();
            _detector = null;
        }
        return Task.CompletedTask;
    }
}
