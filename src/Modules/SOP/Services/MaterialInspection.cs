using Core.Services;
using Modules.SOP.Models;

namespace Modules.SOP.Services;

// One observation window per step. Only detections in the declared camera and ROI qualify.
internal sealed class MaterialInspection
{
    private string[]? _previousMissing;
    private DateTime _since;
    private int _frames;

    public void Reset() => _previousMissing = null;

    public (bool Ready, bool ConfirmedMissing, string Message) Observe(SOPStep step,
        IReadOnlyList<DetectedObject> detections, IReadOnlyDictionary<string, ZoneDefinition> zones, DateTime timestamp)
    {
        bool Seen(RequiredMaterial item)
        {
            ZoneDefinition? zone = null;
            if (!string.IsNullOrEmpty(item.Zone) && !zones.TryGetValue($"{step.CameraId}:{item.Zone}", out zone)) return false;
            return detections.Any(d => d.Confidence >= item.Confidence && d.ClassName.Equals(item.Label, StringComparison.OrdinalIgnoreCase) &&
                (zone == null || (d.PixelBoundingBox.MidX >= zone.X && d.PixelBoundingBox.MidX < zone.X + zone.Width &&
                                  d.PixelBoundingBox.MidY >= zone.Y && d.PixelBoundingBox.MidY < zone.Y + zone.Height)));
        }
        var missing = step.RequiredMaterials.Where(item => !Seen(item)).ToArray();
        var names = missing.Select(m => string.IsNullOrEmpty(m.Zone) ? m.Label : $"{m.Label}@{m.Zone}").ToArray();
        if (_previousMissing == null || !_previousMissing.SequenceEqual(names))
        {
            _since = timestamp;
            _frames = 0;
            _previousMissing = names;
        }
        _frames++;
        var timer = step.PassConditions.FirstOrDefault(c => c.Type == ConditionType.TimeElapsed);
        var seconds = Convert.ToDouble(timer?.Parameters.GetValueOrDefault("RequiredSeconds") ?? 0.0);
        var holdMs = step.PassConditions.Select(c => c.Parameters.GetValueOrDefault("HoldMs") as int? ?? 0).DefaultIfEmpty().Max();
        var frames = step.PassConditions.Select(c => c.StableFrames).DefaultIfEmpty(1).Max();
        var stable = _frames >= frames && (timestamp - _since).TotalMilliseconds >= Math.Max(1000 * seconds, holdMs);
        if (missing.Length == 0) return (stable, false, stable ? "物料齐套" : "物料齐套，等待稳定");
        var anchored = !string.IsNullOrEmpty(step.InspectionAnchor) && step.RequiredMaterials.Any(item =>
            item.Label.Equals(step.InspectionAnchor, StringComparison.OrdinalIgnoreCase) && Seen(item));
        return (false, anchored && stable, anchored ? $"缺少物料 {string.Join(", ", names)}" : "等待工件到位或有效的终检画面");
    }
}
