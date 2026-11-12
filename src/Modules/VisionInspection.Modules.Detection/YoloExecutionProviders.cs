using SkiaSharp;
using YoloDotNet.Enums;
using YoloDotNet.Models;

#if YOLO_EP_CUDA
using YoloDotNet.ExecutionProvider.Cuda;
#else
using YoloDotNet.ExecutionProvider.Cpu;
#endif

namespace VisionInspection.Modules.Detection;

/// <summary>
/// YOLO 执行提供程序（CPU / CUDA）的唯一构造点。
/// <para>
/// YoloDotNet v4 要求一个工程只引用一个 EP 包，所以这里用编译常量
/// <c>YOLO_EP_CUDA</c>（由 <c>Directory.Build.props</c> 的 <c>-p:YoloEp=Cuda</c> 打开）切换：
/// </para>
/// <code>
/// dotnet build                 // 默认 CPU
/// dotnet build -p:YoloEp=Cuda  // CUDA（需 CUDA 12.x + cuDNN 9.x）
/// </code>
/// <para>
/// CUDA 构建的包不提供 CPU 提供程序类（上游即如此），因此 GPU 开关为 false 时仍走 CUDA 会话；
/// CPU 构建忽略 GPU 开关按 CPU 运行。
/// </para>
/// </summary>
public static class YoloExecutionProviders
{
    /// <summary>当前构建是否带 CUDA 支持。</summary>
    public const bool IsCudaBuild =
#if YOLO_EP_CUDA
        true;
#else
        false;
#endif

    /// <summary>构建 YoloDotNet 的初始化选项（预/后处理参数与执行设备一起确定）。</summary>
    public static YoloOptions CreateOptions(string modelPath, bool useGpu, int gpuId = 0)
    {
#if YOLO_EP_CUDA
        return new YoloOptions
        {
            ExecutionProvider = new CudaExecutionProvider(modelPath, gpuId),
            ImageResize = ImageResize.Proportional,
            SamplingOptions = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
        };
#else
        _ = useGpu; // CPU 构建：GPU 开关无意义
        _ = gpuId;
        return new YoloOptions
        {
            ExecutionProvider = new CpuExecutionProvider(modelPath),
            ImageResize = ImageResize.Proportional,
            SamplingOptions = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
        };
#endif
    }
}
