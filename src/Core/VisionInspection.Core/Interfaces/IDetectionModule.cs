using Microsoft.Extensions.Configuration;
using VisionInspection.Core.Models;
using VisionInspection.Core.Services;

namespace VisionInspection.Core.Interfaces;

/// <summary>
/// 检测模块接口 - 所有模块必须实现
/// </summary>
public interface IDetectionModule : IDisposable
{
    /// <summary>
    /// 模块唯一标识（英文，无空格）
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 当前状态
    /// </summary>
    ModuleState State { get; }

    /// <summary>
    /// 初始化模块
    /// </summary>
    Task InitializeAsync(IConfiguration config, ICameraService cameraService);

    /// <summary>
    /// 执行检测
    /// </summary>
    Task<ModuleResult> ProcessAsync(Dictionary<string, CaptureFrame> frames);

    /// <summary>
    /// 关闭模块
    /// </summary>
    Task ShutdownAsync();
}

/// <summary>
/// 模块状态
/// </summary>
public enum ModuleState
{
    Uninitialized,
    Initializing,
    Ready,
    Processing,
    Error
}
