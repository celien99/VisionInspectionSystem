using SkiaSharp;
using Core.Services;
using Modules.SOP.Models;

namespace Modules.SOP.Services;

/// <summary>区域安全帽规则及报警冷却；不改变 SOP 步骤的判定结果。</summary>
public sealed class RegionMonitoringEvaluator
{
    private DateTime _lastAlertTime = DateTime.MinValue;
    private bool _alertActive;

    public string? Evaluate(
        IReadOnlyDictionary<string, List<DetectedObject>> perCameraDetections,
        SOPWorkflow workflow,
        DateTime now)
    {
        var cfg = workflow.Monitoring;
        if (cfg == null || !cfg.Enabled || workflow.Regions.Count == 0) return null;

        var zones = workflow.Regions;
        if (cfg.Regions is { Count: > 0 })
        {
            zones = zones
                .Where(z => cfg.Regions.Contains(z.ZoneId, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        if (zones.Count == 0) return null;

        var personNames = cfg.PersonClasses ?? new List<string>();
        var helmetNames = cfg.HelmetClasses ?? new List<string>();
        if (personNames.Count == 0) return null;

        static bool IsClass(string? name, List<string> names) =>
            !string.IsNullOrEmpty(name) && names.Contains(name, StringComparer.OrdinalIgnoreCase);

        bool violation = false;
        string detail = "";
        foreach (var zoneGroup in zones.GroupBy(z => z.CameraId, StringComparer.OrdinalIgnoreCase))
        {
            if (!perCameraDetections.TryGetValue(zoneGroup.Key, out var detections))
                continue;

            var persons = detections
                .Where(d => d.Confidence >= cfg.MinConfidence && IsClass(d.ClassName, personNames))
                .ToList();
            var helmets = detections
                .Where(d => d.Confidence >= cfg.MinConfidence && IsClass(d.ClassName, helmetNames))
                .ToList();

            foreach (var person in persons)
            {
                var pBox = person.PixelBoundingBox;
                var centerX = pBox.MidX;
                var centerY = pBox.MidY;

                foreach (var zone in zoneGroup)
                {
                    var zoneRect = new SKRect(zone.X, zone.Y, zone.X + zone.Width, zone.Y + zone.Height);
                    if (!zoneRect.Contains(centerX, centerY)) continue;

                    // 头部区域 = 人体框上部 35%
                    var headRect = new SKRect(
                        pBox.Left,
                        pBox.Top,
                        pBox.Right,
                        pBox.Top + pBox.Height * 0.35f);

                    var hasHelmet = helmets.Any(h =>
                    {
                        var hBox = h.PixelBoundingBox;
                        return new SKRect(hBox.Left, hBox.Top, hBox.Right, hBox.Bottom)
                            .IntersectsWith(headRect);
                    });

                    if (!hasHelmet)
                    {
                        violation = true;
                        detail = $"区域[{zone.Name}] 出现未戴安全帽的人(person={person.ClassName}, conf={person.Confidence:F2})";
                        break;
                    }
                }
                if (violation) break;
            }
            if (violation) break;
        }

        if (!violation)
        {
            _alertActive = false;
            return null;
        }
        if (_alertActive && (now - _lastAlertTime).TotalSeconds < cfg.CooldownSeconds) return null;
        _lastAlertTime = now;
        _alertActive = true;
        return detail;
    }

    public void Reset()
    {
        _alertActive = false;
        _lastAlertTime = DateTime.MinValue;
    }
}
