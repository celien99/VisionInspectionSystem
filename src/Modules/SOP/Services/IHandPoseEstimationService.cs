using SkiaSharp;
using Modules.SOP.Models;

namespace Modules.SOP.Services;

/// <summary>
/// 手部姿态估计服务接口
/// </summary>
public interface IHandPoseEstimationService
{
    Task<HandPoseEstimationResult> DetectHandsAsync(SKBitmap image);
    bool IsInitialized { get; }
    Task InitializeAsync(HandPoseEstimationConfig config);
    Task ShutdownAsync();
}
