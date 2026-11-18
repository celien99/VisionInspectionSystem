using SkiaSharp;
using VisionInspection.Core.Services;
using VisionInspection.Modules.SOP;
using VisionInspection.Modules.SOP.Models;

namespace VisionInspection.UI.Services;

/// <summary>检测叠加层及其显示缓存；不持有相机帧，不控制推理。</summary>
public sealed class DetectionOverlayRenderer
{
    private readonly object _violationToastLock = new();
    private readonly List<ViolationToast> _activeViolations = new();
    private sealed record ViolationToast(DateTime Time, string TypeName, string Description);
    private readonly object _resultLock = new();
    private Dictionary<string, List<DetectedObject>> _perCameraResults = new();
    private HandPoseEstimationResult? _hands;
    private string _handCameraId = "main_camera";
    private int _noHandsFrames;

    public void UpdateSopResult(SOPModuleResult result)
    {
        lock (_resultLock)
        {
            _perCameraResults = result.PerCameraDetections;
            if (result.HandPoseResult is { Hands.Count: > 0 })
            {
                _hands = result.HandPoseResult;
                _handCameraId = result.HandPoseCameraId;
                _noHandsFrames = 0;
            }
            else if (++_noHandsFrames >= 8)
                _hands = null;
        }
    }

    public void DrawSop(SKCanvas canvas, SKImageInfo info, string cameraId)
    {
        lock (_resultLock)
        {
            if (_perCameraResults.TryGetValue(cameraId, out var objects))
                DrawDetectedObjects(canvas, objects, 18f);
            if (string.Equals(cameraId, _handCameraId, StringComparison.OrdinalIgnoreCase) && _hands != null)
                DrawHandPoses(canvas, _hands);
        }
        if (cameraId == CameraManager.PrimaryCameraId) DrawViolationBanner(canvas, info);
    }

    public void AddViolation(ViolationRecord violation)
    {
        lock (_violationToastLock)
        {
            _activeViolations.Add(new(DateTime.Now, ViolationTypeText(violation.Type), violation.Description));
            if (_activeViolations.Count > 3) _activeViolations.RemoveAt(0);
        }
    }

    public void ClearViolations()
    {
        lock (_violationToastLock) _activeViolations.Clear();
    }

    public void ResetSop()
    {
        lock (_resultLock)
        {
            _perCameraResults = new();
            _hands = null;
            _noHandsFrames = 0;
        }
        ClearViolations();
    }

    /// <summary>
    /// 违规提示横幅：主相机画面顶部红色横幅显示最新违规内容（类型 + 描述），
    /// 其下以小字列出更早的违规（最多 2 条）。最新违规出现后前 2 秒闪烁高亮，之后常驻
    /// 直到停止/复位 SOP——违规提示必须醒目且不消失，符合工业场景"发生即告警"的要求。
    /// 由 OnCameraFrameGrabbed（采集线程）逐帧调用，缓存读取加锁。
    /// </summary>
    private void DrawViolationBanner(SKCanvas canvas, SKImageInfo info)
    {
        List<ViolationToast> toasts;
        lock (_violationToastLock)
        {
            if (_activeViolations.Count == 0) return;
            toasts = new List<ViolationToast>(_activeViolations);
        }

        try
        {
            using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei");
            var latest = toasts[toasts.Count - 1];
            double age = (DateTime.Now - latest.Time).TotalSeconds;

            // 最新违规前 2 秒透明度闪烁（~1.25Hz）引起注意，之后稳定常驻
            byte alpha = age < 2 ? (byte)(190 + 65 * Math.Abs(Math.Sin(age * Math.PI * 2.5))) : (byte)215;

            const float bannerH = 56f;
            using (var bg = new SKPaint { Color = new SKColor(204, 0, 0, alpha), Style = SKPaintStyle.Fill })
            {
                canvas.DrawRect(0, 0, info.Width, bannerH, bg);
            }
            // 左侧橙色警示竖条
            using (var bar = new SKPaint { Color = new SKColor(255, 140, 0, alpha), Style = SKPaintStyle.Fill })
            {
                canvas.DrawRect(0, 0, 6, bannerH, bar);
            }

            string latestText = $"⚠ 违规（{latest.TypeName}）：{latest.Description}";
            using (var big = new SKPaint
            {
                Color = SKColors.White,
                TextSize = 26,
                IsAntialias = true,
                FakeBoldText = true,
                Typeface = typeface
            })
            {
                canvas.DrawText(latestText, 16, 37, big);
            }

            // 历史违规（更早的最多 2 条）：横幅下方小字，带时间
            using (var small = new SKPaint
            {
                Color = new SKColor(255, 255, 255, 235),
                TextSize = 17,
                IsAntialias = true,
                FakeBoldText = true,
                Typeface = typeface
            })
            {
                float y = bannerH + 22;
                for (int i = toasts.Count - 2; i >= 0 && i >= toasts.Count - 3; i--)
                {
                    var t = toasts[i];
                    canvas.DrawText($"{t.Time:HH:mm:ss} [{t.TypeName}] {t.Description}", 16, y, small);
                    y += 24;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"绘制违规横幅异常: {ex.Message}");
        }
    }

    /// <summary>违规类型的中文显示名</summary>
    private static string ViolationTypeText(ViolationType type) => type switch
    {
        ViolationType.SkipStep => "跳过工序",
        ViolationType.WrongOrder => "顺序错误",
        ViolationType.Timeout => "超时",
        ViolationType.ForbiddenObject => "出现违禁物",
        ViolationType.ObjectRemoved => "物体被移除",
        ViolationType.ZoneIntrusion => "区域闯入",
        ViolationType.MissingRequiredObject => "漏放/缺料",
        _ => "违规"
    };

    /// <summary>
    /// 手部 21 个关键点全画（MediaPipe 全部点位）。之前只画 11 个并刻意缩小半径，
    /// 叠加层又被缩放到窗口，结果是"几乎看不见"。
    /// </summary>
    private static readonly HandKeypointType[] HandLandmarks = Enum.GetValues<HandKeypointType>();

    /// <summary>MediaPipe 21 点标准骨架：每根手指 腕→MCP→PIP→DIP→指尖。</summary>
    private static readonly (HandKeypointType, HandKeypointType)[] HandSkeleton = new[]
    {
        (HandKeypointType.Wrist, HandKeypointType.ThumbCMC),
        (HandKeypointType.ThumbCMC, HandKeypointType.ThumbMCP),
        (HandKeypointType.ThumbMCP, HandKeypointType.ThumbIP),
        (HandKeypointType.ThumbIP, HandKeypointType.ThumbTip),
        (HandKeypointType.Wrist, HandKeypointType.IndexFingerMCP),
        (HandKeypointType.IndexFingerMCP, HandKeypointType.IndexFingerPIP),
        (HandKeypointType.IndexFingerPIP, HandKeypointType.IndexFingerDIP),
        (HandKeypointType.IndexFingerDIP, HandKeypointType.IndexFingerTip),
        (HandKeypointType.Wrist, HandKeypointType.MiddleFingerMCP),
        (HandKeypointType.MiddleFingerMCP, HandKeypointType.MiddleFingerPIP),
        (HandKeypointType.MiddleFingerPIP, HandKeypointType.MiddleFingerDIP),
        (HandKeypointType.MiddleFingerDIP, HandKeypointType.MiddleFingerTip),
        (HandKeypointType.Wrist, HandKeypointType.RingFingerMCP),
        (HandKeypointType.RingFingerMCP, HandKeypointType.RingFingerPIP),
        (HandKeypointType.RingFingerPIP, HandKeypointType.RingFingerDIP),
        (HandKeypointType.RingFingerDIP, HandKeypointType.RingFingerTip),
        (HandKeypointType.Wrist, HandKeypointType.PinkyMCP),
        (HandKeypointType.PinkyMCP, HandKeypointType.PinkyPIP),
        (HandKeypointType.PinkyPIP, HandKeypointType.PinkyDIP),
        (HandKeypointType.PinkyDIP, HandKeypointType.PinkyTip)
    };

    /// <summary>
    /// 绘制手部姿态（21 点完整骨架，尺寸随帧分辨率缩放）
    /// </summary>
    private void DrawHandPoses(SKCanvas canvas, HandPoseEstimationResult handResult)
    {
        // 保存画布状态
        canvas.Save();

        // 注意：DWPose输出的坐标与SkiaSharp坐标系一致（原点在左上角）
        // 不需要翻转Y轴

        // 叠加层画在原始帧上、再整体缩放到窗口显示：所有尺寸必须随帧分辨率缩放，
        // 否则 1080p/4K 帧缩到窗口后线宽只剩 1px（"姿态看不明显"的直接原因）。
        float s = OverlayScale(canvas);

        // 骨架线画笔（黑色底衬 + 亮绿线，保证任何背景都看得清）
        using var skeletonOutlinePaint = new SKPaint
        {
            Color = SKColors.Black.WithAlpha(170),
            StrokeWidth = 5f * s,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };
        using var skeletonPaint = new SKPaint
        {
            Color = SKColors.LimeGreen,
            StrokeWidth = 3f * s,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        // 指尖关键点画笔（红色）
        using var tipPaint = new SKPaint
        {
            Color = SKColors.Red,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        // 关节关键点画笔（黄色）
        using var jointPaint = new SKPaint
        {
            Color = SKColors.Yellow,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        // 手腕关键点画笔（青色）
        using var wristPaint = new SKPaint
        {
            Color = SKColors.Cyan,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        // 关键点黑色外圈：亮色圆点在任何背景上都有轮廓
        using var keypointOutlinePaint = new SKPaint
        {
            Color = SKColors.Black.WithAlpha(200),
            StrokeWidth = 1.5f * s,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        // 关键点标签画笔
        using var labelPaint = new SKPaint
        {
            Color = SKColors.Yellow,
            TextSize = 13 * s,
            IsAntialias = true,
            FakeBoldText = true,
            Typeface = OverlayTypeface
        };

        // 手部边界框画笔（青色）
        using var handBoxPaint = new SKPaint
        {
            Color = SKColors.Cyan,
            StrokeWidth = 2.5f * s,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };
        using var handBoxOutlinePaint = new SKPaint
        {
            Color = SKColors.Black.WithAlpha(170),
            StrokeWidth = 4.5f * s,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        foreach (var hand in handResult.Hands)
        {
            // 绘制手部边界框
            var bbox = hand.BoundingBox;
            canvas.DrawRect(bbox, handBoxOutlinePaint);
            canvas.DrawRect(bbox, handBoxPaint);

            // 完整 21 点骨架
            DrawHandSkeleton(canvas, hand, skeletonPaint, skeletonOutlinePaint);

            foreach (var type in HandLandmarks)
            {
                var kp = hand.GetKeypoint(type);
                if (kp == null || kp.Confidence < SkeletonConfidenceThreshold) continue;

                bool isTip = type == HandKeypointType.ThumbTip ||
                             type == HandKeypointType.IndexFingerTip ||
                             type == HandKeypointType.MiddleFingerTip ||
                             type == HandKeypointType.RingFingerTip ||
                             type == HandKeypointType.PinkyTip;

                bool isWrist = type == HandKeypointType.Wrist;

                // 指尖/手腕画大一点，中间关节略小：既能看清手势，又不糊成一片
                float radius = isTip ? 6 * s : (isWrist ? 7 * s : 4 * s);
                var paint = isTip ? tipPaint : (isWrist ? wristPaint : jointPaint);

                canvas.DrawCircle(kp.X, kp.Y, radius + 1.5f * s, keypointOutlinePaint);
                canvas.DrawCircle(kp.X, kp.Y, radius, paint);

                // 只在指尖旁边绘制标签，避免杂乱
                if (isTip)
                {
                    canvas.DrawText(type.ToString(), kp.X + 12 * s, kp.Y, labelPaint);
                }
            }
        }

        // 恢复画布状态
        canvas.Restore();
    }

    /// <summary>
    /// 绘制手部骨架线（MediaPipe 21 点标准连接；先画黑色底衬再画彩色线）
    /// </summary>
    // 骨架绘制置信度阈值（低于此值的关键点不绘制，减少闪烁）
    public float SkeletonConfidenceThreshold { get; set; } = 0.1f;

    private void DrawHandSkeleton(SKCanvas canvas, HandPose hand, SKPaint paint, SKPaint outlinePaint)
    {
        foreach (var (startType, endType) in HandSkeleton)
        {
            var start = hand.GetKeypoint(startType);
            var end = hand.GetKeypoint(endType);

            if (start == null || end == null) continue;
            if (start.Confidence < SkeletonConfidenceThreshold || end.Confidence < SkeletonConfidenceThreshold) continue;

            canvas.DrawLine(start.X, start.Y, end.X, end.Y, outlinePaint);
            canvas.DrawLine(start.X, start.Y, end.X, end.Y, paint);
        }
    }

    /// <summary>
    /// 绘制检测结果到图像
    /// </summary>
    // COCO 17关键点骨骼连接（0-indexed：nose, left_eye, right_eye, left_ear, right_ear,
    // left_shoulder, right_shoulder, left_elbow, right_elbow, left_wrist, right_wrist,
    // left_hip, right_hip, left_knee, right_knee, left_ankle, right_ankle）
    private static readonly int[][] COCO_SKELETON =
    {
        new[] { 15, 13 }, new[] { 13, 11 }, new[] { 16, 14 }, new[] { 14, 12 },
        new[] { 11, 12 }, new[] { 5, 11 }, new[] { 6, 12 }, new[] { 5, 6 },
        new[] { 5, 7 }, new[] { 7, 9 }, new[] { 6, 8 }, new[] { 8, 10 },
        new[] { 1, 3 }, new[] { 0, 2 }, new[] { 0, 1 }, new[] { 2, 4 }, new[] { 3, 5 }
    };
    // 关键点绘制的最低置信度
    private const float POSE_KEYPOINT_CONF = 0.3f;
    // 分割掩膜颜色调色板（按类别索引取色，半透明叠加）
    private static readonly SKColor[] SEG_MASK_COLORS =
    {
        new SKColor(0, 200, 255, 110),   // 青
        new SKColor(255, 120, 0, 110),   // 橙
        new SKColor(0, 220, 120, 110),   // 绿
        new SKColor(255, 80, 160, 110),  // 粉
        new SKColor(180, 120, 255, 110), // 紫
        new SKColor(255, 220, 0, 110),   // 黄
        new SKColor(80, 160, 255, 110),  // 蓝
        new SKColor(255, 80, 80, 110)    // 红
    };

    public SKBitmap DrawDetectionResults(SKBitmap originalImage, List<DetectedObject> objects, float textSize = 24f)
    {
        // 创建副本
        var resultBitmap = originalImage.Copy();
        using var canvas = new SKCanvas(resultBitmap);
        DrawDetectedObjects(canvas, objects, textSize);
        return resultBitmap;
    }

    /// <summary>
    /// 叠加层统一缩放系数。
    /// <para>
    /// 叠加层是画在<b>原始帧</b>上、再被整体缩放到窗口显示的：
    /// 1920/3072 宽的帧缩到 ~1000px 宽的画面区只剩 1/2~1/3。
    /// 所以线宽/半径/字号必须是"相对帧宽"的，按 1280 宽为基准换算，
    /// 否则画面里的框和骨架会细到 1px（这是"检测框/姿态不突出"的根因，与模型无关）。
    /// </para>
    /// </summary>
    private static float OverlayScale(SKCanvas canvas)
    {
        var width = canvas.LocalClipBounds.Width;
        if (width <= 0) width = 1280;
        return Math.Clamp(width / 1280f, 1f, 4f);
    }

    /// <summary>叠加层文字字体（首选带中文字形的字体，避免中文类别名变方块）</summary>
    private static readonly SKTypeface OverlayTypeface = ResolveOverlayTypeface();

    private static SKTypeface ResolveOverlayTypeface()
    {
        foreach (var name in new[] { "Microsoft YaHei", "PingFang SC", "Segoe UI", "Arial" })
        {
            var typeface = SKTypeface.FromFamilyName(name);
            if (typeface != null) return typeface;
        }
        return SKTypeface.Default;
    }

    /// <summary>
    /// 在已有画布上叠加绘制检测结果（边界框 + 标签 + 姿态骨骼 + 分割掩膜）。
    /// <para>提取自 <see cref="DrawDetectionResults"/>，便于在相机最新帧上直接叠加（与 SOP 渲染架构一致），
    /// 避免「无框原始帧」与「有框帧」交替造成的实时检测闪烁。</para>
    /// </summary>
    public void DrawDetectedObjects(SKCanvas canvas, List<DetectedObject> objects, float textSize = 24f)
    {
        float s = OverlayScale(canvas);
        float stroke = 3.5f * s;

        // 黑色底衬：浅色/花色背景上也能看清框线
        using var outlinePaint = new SKPaint
        {
            Color = SKColors.Black.WithAlpha(170),
            StrokeWidth = stroke + 2.5f * s,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        foreach (var obj in objects)
        {
            // 根据置信度选择颜色
            var color = obj.Confidence > 0.7 ? SKColors.LimeGreen :
                       obj.Confidence > 0.5 ? SKColors.Yellow : SKColors.Red;

            using var paint = new SKPaint
            {
                Color = color,
                StrokeWidth = stroke,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };

            // 绘制边界框（使用PixelBoundingBox）
            canvas.DrawRect(obj.PixelBoundingBox, outlinePaint);
            canvas.DrawRect(obj.PixelBoundingBox, paint);

            // 标签（字号随分辨率缩放）
            using var textPaint = new SKPaint
            {
                Color = color,
                TextSize = textSize * s,
                IsAntialias = true,
                FakeBoldText = true,
                Typeface = OverlayTypeface
            };

            var label = $"{obj.ClassName} {obj.Confidence:P0}";
            var textBounds = new SKRect();
            textPaint.MeasureText(label, ref textBounds);

            float pad = 4f * s;
            var box = obj.PixelBoundingBox;
            var bgRect = new SKRect(
                box.Left,
                box.Top - textBounds.Height - 2 * pad,
                box.Left + textBounds.Width + 2 * pad,
                box.Top);

            using var bgPaint = new SKPaint
            {
                Color = color.WithAlpha(210),
                Style = SKPaintStyle.Fill
            };
            canvas.DrawRect(bgRect, bgPaint);

            // 绘制标签文字
            textPaint.Color = SKColors.White;
            canvas.DrawText(label, box.Left + pad, box.Top - pad, textPaint);

            // 绘制人体姿态骨骼（仅姿态估计模型会带 KeyPoints）
            if (obj.KeyPoints != null && obj.KeyPoints.Count > 0)
            {
                DrawPoseSkeleton(canvas, obj.KeyPoints);
            }

            // 绘制实例分割掩膜（仅分割模型会带 Mask）
            if (obj.Mask != null)
            {
                var maskColor = SEG_MASK_COLORS[obj.ClassId % SEG_MASK_COLORS.Length];
                // 掩膜按原始检测框尺寸位打包，必须用 MaskBox（而非平滑后的 PixelBoundingBox）绘制，否则错位/变形
                var maskBox = obj.MaskBox ?? obj.PixelBoundingBox;
                DrawSegmentationMask(canvas, obj.Mask,
                    maskBox.Left, maskBox.Top,
                    maskBox.Width, maskBox.Height, maskColor);
            }
        }
    }

    /// <summary>
    /// 在画布上绘制 COCO 姿态关键点与骨骼连线
    /// </summary>
    private void DrawPoseSkeleton(SKCanvas canvas, List<VisionInspection.Core.Services.KeyPoint> kps)
    {
        // 按置信度过滤不可靠的关键点
        var valid = kps.Where(k => k.Confidence >= POSE_KEYPOINT_CONF).ToList();
        if (valid.Count == 0) return;

        float s = OverlayScale(canvas);
        float lineWidth = 3f * s;

        // 先绘制骨骼连线（黑色底衬 + 亮绿线）
        using var lineOutline = new SKPaint
        {
            Color = SKColors.Black.WithAlpha(170),
            StrokeWidth = lineWidth + 2.5f * s,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };
        using var linePaint = new SKPaint
        {
            Color = SKColors.Lime,
            StrokeWidth = lineWidth,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };
        foreach (var pair in COCO_SKELETON)
        {
            var a = valid.FirstOrDefault(k => k.Index == pair[0]);
            var b = valid.FirstOrDefault(k => k.Index == pair[1]);
            if (a != null && b != null)
            {
                canvas.DrawLine(a.X, a.Y, b.X, b.Y, lineOutline);
                canvas.DrawLine(a.X, a.Y, b.X, b.Y, linePaint);
            }
        }

        // 再绘制关键点（黄点 + 黑色描边）
        using var pointPaint = new SKPaint
        {
            Color = SKColors.Yellow,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        using var pointOutline = new SKPaint
        {
            Color = SKColors.Black,
            StrokeWidth = 1.5f * s,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };
        float radius = 4f * s;
        foreach (var k in valid)
        {
            canvas.DrawCircle(k.X, k.Y, radius, pointPaint);
            canvas.DrawCircle(k.X, k.Y, radius, pointOutline);
        }
    }

    /// <summary>
    /// 在画布上叠加实例分割掩膜（半透明着色区域）
    /// </summary>
    private void DrawSegmentationMask(SKCanvas canvas, byte[]? packedMask, int boxX, int boxY, int boxW, int boxH, SKColor color)
    {
        if (packedMask == null || packedMask.Length == 0 || boxW <= 0 || boxH <= 0)
            return;

        // 将位打包掩膜解包为彩色半透明位图，并绘制到边界框位置
        using var maskBmp = UnpackSegmentationMask(packedMask, boxW, boxH, color);
        canvas.DrawBitmap(maskBmp, boxX, boxY);
    }

    /// <summary>
    /// 把位打包的像素掩膜解包为彩色半透明 SKBitmap（尺寸 = 边界框宽高）。
    /// 位序：byteIndex = i&gt;&gt;3, bitIndex = i &amp; 7（沿用原 YoloDotNet 输出格式）。
    /// </summary>
    private unsafe SKBitmap UnpackSegmentationMask(byte[] packed, int w, int h, SKColor color)
    {
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        int total = w * h;
        byte* ptr = (byte*)bmp.GetPixels().ToPointer();

        for (int i = 0; i < total; i++)
        {
            int byteIndex = i >> 3;     // i / 8
            int bitIndex = i & 7;       // i % 8
            bool isOn = (packed[byteIndex] & (1 << bitIndex)) != 0;

            int offset = i * 4;
            if (isOn)
            {
                ptr[offset] = color.Blue;
                ptr[offset + 1] = color.Green;
                ptr[offset + 2] = color.Red;
                ptr[offset + 3] = color.Alpha;
            }
            else
            {
                ptr[offset] = 0;
                ptr[offset + 1] = 0;
                ptr[offset + 2] = 0;
                ptr[offset + 3] = 0;
            }
        }
        return bmp;
    }

}
