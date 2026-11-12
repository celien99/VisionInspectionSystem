using SkiaSharp;
using VisionInspection.Core.Services;
using YoloDotNet;
using YoloDotNet.Models;

namespace VisionInspection.Modules.Detection;

/// <summary>
/// 单个 YOLO 模型实例的适配器。
/// <para>
/// <b>边界约定</b>：SOP 引擎、条件评估器、违规检测器、UI 只认识
/// <see cref="DetectedObject"/>，不再出现 YoloDotNet 的 <c>Yolo</c> / <c>ObjectDetection</c>。
/// 需要换推理后端（ONNX Runtime 直连、RKNN、TensorRT……）时，改这个文件即可，
/// 不要在业务代码里再 <c>new Yolo(...)</c>。
/// </para>
/// </summary>
public sealed class YoloModel : IDisposable
{
    private readonly Yolo _yolo;

    private YoloModel(Yolo yolo) => _yolo = yolo;

    /// <summary>
    /// 加载模型。失败（文件损坏、CUDA 不可用等）返回 null 并给出原因，由调用方决定降级策略。
    /// </summary>
    /// <param name="resolvedPath">已解析成绝对路径的 .onnx</param>
    /// <param name="useGpu">是否优先 CUDA。CUDA 初始化失败不会自动回退，需要调用方重试 CPU</param>
    /// <param name="gpuId">CUDA 设备序号</param>
    public static YoloModel? Load(string resolvedPath, bool useGpu, int gpuId, out string? error)
    {
        error = null;
        try
        {
            var options = YoloExecutionProviders.CreateOptions(resolvedPath, useGpu, gpuId);
            return new YoloModel(new Yolo(options));
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 物体检测，返回与推理后端无关的 <see cref="DetectedObject"/> 列表。
    /// </summary>
    public List<DetectedObject> Detect(SKBitmap image, float confidence, float iou)
    {
        var results = _yolo.RunObjectDetection(image, confidence: confidence, iou: iou);

        var objects = new List<DetectedObject>(results.Count);
        foreach (var r in results)
        {
            objects.Add(new DetectedObject
            {
                ClassId = r.Label.Index,
                ClassName = r.Label.Name,
                Confidence = (float)r.Confidence,
                BoundingBox = new[]
                {
                    (float)r.BoundingBox.Left, (float)r.BoundingBox.Top,
                    (float)r.BoundingBox.Width, (float)r.BoundingBox.Height
                },
                PixelBoundingBox = r.BoundingBox
            });
        }
        return objects;
    }

    public void Dispose() => _yolo.Dispose();
}
