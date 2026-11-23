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
        var slots = step.RequiredMaterials;
        var candidates = slots.Select(item =>
        {
            zones.TryGetValue($"{step.CameraId}:{item.Zone}", out var zone);
            return Enumerable.Range(0, detections.Count).Where(i =>
                detections[i].Confidence >= item.Confidence && detections[i].ClassName.Equals(item.Label, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(item.Zone) || zone?.ContainsCenter(detections[i].PixelBoundingBox) == true)).ToArray();
        }).ToArray();
        var missing = Enumerable.Range(0, slots.Count).Where(i => candidates[i].Length == 0).ToArray();
        // Assign only populated slots; genuinely empty slots can still be identified by name.
        if (!HasUnambiguousCoverage(candidates, detections.Count))
        {
            Reset();
            return (false, false, "物料与槽位的归属不明确，需要复核");
        }
        var names = missing.Select(i => string.IsNullOrEmpty(slots[i].Zone) ? slots[i].Label : $"{slots[i].Label}@{slots[i].Zone}").ToArray();
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
        var anchored = !string.IsNullOrEmpty(step.InspectionAnchor) && Enumerable.Range(0, slots.Count).Any(i =>
            slots[i].Label.Equals(step.InspectionAnchor, StringComparison.OrdinalIgnoreCase) && candidates[i].Length > 0);
        return (false, anchored && stable, anchored ? $"缺少物料 {string.Join(", ", names)}" : "等待工件到位或有效的终检画面");
    }

    private static bool HasUnambiguousCoverage(int[][] candidates, int detectionCount)
    {
        bool CanCover(int reservedSlot = -1, int reservedDetection = -1)
        {
            var owner = Enumerable.Repeat(-1, detectionCount).ToArray();
            for (int slot = 0; slot < candidates.Length; slot++)
                if (slot != reservedSlot && candidates[slot].Length > 0 && !Assign(slot, new bool[detectionCount])) return false;
            return true;

            // Displace an earlier assignment if that slot has an alternative.
            bool Assign(int slot, bool[] seen)
            {
                foreach (int detection in candidates[slot])
                {
                    if (detection == reservedDetection || seen[detection]) continue;
                    seen[detection] = true;
                    if (owner[detection] >= 0 && !Assign(owner[detection], seen)) continue;
                    owner[detection] = slot;
                    return true;
                }
                return false;
            }
        }

        if (!CanCover()) return false;
        var possibleOwners = candidates.SelectMany((ids, slot) => ids.Select(id => (id, slot))).ToLookup(p => p.id, p => p.slot);
        foreach (var shared in possibleOwners.Where(group => group.Count() > 1))
            if (shared.Where(slot => CanCover(slot, shared.Key)).Skip(1).Any()) return false;
        return true;
    }
}
