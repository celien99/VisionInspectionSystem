using SkiaSharp;

namespace VisionInspection.Modules.Detection;

/// <summary>
/// OpenCV 视频帧 → SKBitmap 的唯一转换路径（视频预览与视频推理共用）。
/// <para>
/// 直接拷像素，避免原先"Mat → System.Drawing.Bitmap → PNG 编码 → SKBitmap.Decode"的往返：
/// 一张 1080p 帧多出一次编码 + 一次解码，而且多一套像素格式规则。
/// </para>
/// </summary>
public static class VideoFrameConverter
{
    /// <summary>
    /// 把 BGR/GRAY 的 Mat 转成 Bgra8888 位图。
    /// <paramref name="reuse"/> 尺寸一致时复用它已有的像素缓冲（逐帧推理不要每帧分配 8MB）。
    /// </summary>
    public static SKBitmap ToSKBitmap(OpenCvSharp.Mat frame, SKBitmap? reuse = null)
    {
        using var bgra = new OpenCvSharp.Mat();

        // 常规视频是 BGR24；灰度视频是单通道，按 GRAY2BGRA 转，否则 CvtColor 直接抛异常
        var code = frame.Channels() == 1
            ? OpenCvSharp.ColorConversionCodes.GRAY2BGRA
            : OpenCvSharp.ColorConversionCodes.BGR2BGRA;
        OpenCvSharp.Cv2.CvtColor(frame, bgra, code);

        if (reuse == null || reuse.Width != bgra.Width || reuse.Height != bgra.Height)
            reuse = new SKBitmap(new SKImageInfo(bgra.Width, bgra.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));

        long srcStep = bgra.Step();
        long dstStep = reuse.RowBytes;
        long rowBytes = Math.Min(srcStep, dstStep);

        unsafe
        {
            byte* src = (byte*)bgra.Data;
            byte* dst = (byte*)reuse.GetPixels();
            for (int y = 0; y < bgra.Height; y++)
                Buffer.MemoryCopy(src + y * srcStep, dst + y * dstStep, dstStep, rowBytes);
        }

        return reuse;
    }
}
