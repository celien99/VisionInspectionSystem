using Core.Services;
using System.Diagnostics;
using SkiaSharp;
using Modules.SOP.Models;

namespace Modules.SOP.Services;

/// <summary>
/// 步骤条件评估器
/// </summary>
public class StepConditionEvaluator
{
    private readonly SOPStateMachine _stateMachine;
    private readonly Dictionary<string, ZoneDefinition> _zones;

    // 调试日志：与 SOPModule.DebugLog 写同一文件 sop_module_debug.log，方便一次性查看
    private static readonly string DebugLogFile = AppPaths.Log("sop_module_debug.log");
    private static readonly object _dbgLock = new();
    private static void Dbg(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [StepConditionEvaluator] {msg}";
        Console.WriteLine(line);
        Debug.WriteLine(line);
        if (LogThrottle.Allow("sop_condition", 10))
        {
            lock (_dbgLock)
            {
                try { File.AppendAllText(DebugLogFile, line + Environment.NewLine); } catch { }
            }
        }
    }

    public StepConditionEvaluator(SOPStateMachine stateMachine, IReadOnlyList<ZoneDefinition>? zones = null)
    {
        _stateMachine = stateMachine;
        _zones = (zones ?? new List<ZoneDefinition>())
            .ToDictionary(z => ZoneKey(z.CameraId, z.ZoneId), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 动态更新区域定义（用于热切换配置）
    /// </summary>
    /// <summary>
    /// 【黄金数据集录制专用】当前生效的区域定义只读快照。
    /// 录制器据此把 zones 写进 meta.json —— 否则一旦现场重新标定区域，
    /// 历史黄金基线就永久失效（2026-10-07 实测踩到）。
    /// </summary>
    public IReadOnlyDictionary<string, ZoneDefinition> Zones => _zones;

    public void UpdateZones(IReadOnlyList<ZoneDefinition> zones)
    {
        _zones.Clear();
        foreach (var zone in zones)
        {
            _zones[ZoneKey(zone.CameraId, zone.ZoneId)] = zone;
        }
    }

    /// <summary>
    /// 评估步骤的所有条件（物体 + 手部姿态）
    /// </summary>
    public ConditionEvaluationResult EvaluateConditions(
        SOPStep step,
        List<DetectedObject> detections,
        HandPoseEstimationResult? handResult,
        DateTime timestamp,
        string cameraId = "main_camera")
    {
        if (!_windows.TryGetValue(step, out var window)) _windows.Add(step, window = new());
        var hands = UniqueHands(handResult).Where(h => h.IsValidGesture(8)).ToArray();
        window.ObserveHands(hands, step);

        if (step.PassConditions.Count == 0)
        {
            return new ConditionEvaluationResult { IsPass = false, Message = "步骤没有通过条件" };
        }

        var results = new List<ConditionCheckResult>();

        // [DEBUG] 评估入口摘要：每帧打印当前步骤的关键输入，便于排查"卡在某一步"。
        // 一帧一行，格式固定，方便从 stdout grep。
        if (step.PassConditions.Any(c =>
            c.Type == ConditionType.HandMoveFromTo ||
            c.Type == ConditionType.HandInRegion ||
            c.Type == ConditionType.HandNotInRegion ||
            c.Type == ConditionType.HandStable))
        {
            int handCount = handResult?.Hands?.Count ?? 0;
            string handSummary = handCount == 0
                ? "none"
                : string.Join("|", handResult!.Hands.Select(h =>
                    $"{h.HandType}(kx={h.Keypoints.Count},bbox={h.BoundingBox.Left:F0},{h.BoundingBox.Top:F0}-{h.BoundingBox.Right:F0},{h.BoundingBox.Bottom:F0})"));
            string detSummary = detections.Count == 0
                ? "none"
                : string.Join("|", detections.Select(d =>
                    $"{d.ClassName}({d.Confidence:F2},[{d.PixelBoundingBox.Left:F0},{d.PixelBoundingBox.Top:F0}-{d.PixelBoundingBox.Right:F0},{d.PixelBoundingBox.Bottom:F0}])"));
            Dbg($"step='{step.StepName}' hands={handSummary} dets={detSummary}");
        }

        var nextMatches = new Dictionary<(int Condition, int Hand), (DateTime Since, int Frames)>();
        foreach (var condition in step.PassConditions)
        {
            var index = results.Count;
            var isHand = condition.Type is ConditionType.HandInRegion or ConditionType.HandNotInRegion or
                ConditionType.HandMoveFromTo or ConditionType.HandStable or ConditionType.HandNearObject;
            ConditionCheckResult? best = null;
            if (isHand)
            {
                foreach (var hand in MatchingHands(condition, hands))
                {
                    var candidate = condition.Type switch
                    {
                        ConditionType.HandNearObject => CheckHandNearObject(condition, detections, hand),
                        ConditionType.HandMoveFromTo => window.Actions.Observe(condition, hand, handResult!.Hands, detections, _stateMachine.TrackFor,
                            GetZoneDefinition(condition.Parameters.GetValueOrDefault("FromRegion") as string ?? "", cameraId),
                            GetZoneDefinition(condition.Parameters.GetValueOrDefault("ToRegion") as string ?? "", cameraId)),
                        _ => CheckHandCondition(condition, hand, cameraId, window)
                    };
                    Gate(candidate, hand.TrackId);
                    if (best == null || candidate.IsMet || (!best.IsMet && candidate.WaitingForStability)) best = candidate;
                }
            }
            else
            {
                best = condition.Type switch
                {
                    ConditionType.ObjectPresent => CheckObjectPresent(condition, detections),
                    ConditionType.ObjectInZone => CheckObjectInZone(condition, detections, cameraId),
                    ConditionType.ObjectStable => CheckObjectStable(condition, cameraId, timestamp),
                    ConditionType.ObjectAbsent => CheckObjectAbsent(condition, detections),
                    ConditionType.SequenceComplete => CheckSequenceComplete(condition),
                    ConditionType.TimeElapsed => CheckTimeElapsed(condition, timestamp),
                    _ => new() { Message = $"未知条件类型: {condition.Type}" }
                };
                Gate(best, -1);
            }
            results.Add(best ?? new() { Message = "未检测到符合条件的手" });

            void Gate(ConditionCheckResult result, int handId)
            {
                if (!result.IsMet) return;
                // Stable / Move / Time have their own motion or temporal criteria.
                var required = condition.Type is ConditionType.HandStable or ConditionType.ObjectStable or
                    ConditionType.HandMoveFromTo or ConditionType.TimeElapsed or ConditionType.SequenceComplete ? 1 : condition.StableFrames;
                var hold = condition.Parameters.GetValueOrDefault("HoldMs") as int? ?? 0;
                if (required <= 1 && hold == 0) return;
                var key = (index, handId);
                var match = window.Matches.TryGetValue(key, out var prior) ? prior : (Since: timestamp, Frames: 0);
                nextMatches[key] = (match.Since, ++match.Frames);
                if (match.Frames < required || (timestamp - match.Since).TotalMilliseconds < hold)
                {
                    result.IsMet = false;
                    result.WaitingForStability = true;
                    result.Message += $"（连续 {match.Frames}/{required} 帧，要求保持 {hold} ms）";
                }
            }
        }
        window.Matches = nextMatches;
        var failed = results.FindIndex(r => !r.IsMet);
        return new ConditionEvaluationResult
        {
            IsPass = failed < 0,
            FailedCondition = failed < 0 ? null : step.PassConditions[failed],
            Message = failed < 0 ? $"步骤 '{step.StepName}' 所有条件满足" : results[failed].Message,
            CheckResults = results
        };
    }

    #region 手部动作条件评估

    // Candidate and active steps observe the same frames but never share accumulators.
    private readonly Dictionary<SOPStep, EvidenceWindow> _windows = new();
    private sealed class EvidenceWindow
    {
        public Dictionary<(int Condition, int Hand), (DateTime Since, int Frames)> Matches = new();
        public readonly Dictionary<int, Queue<SKPoint>> Centers = new();
        public readonly HandObjectTrajectory Actions = new();

        public void ObserveHands(HandPose[] hands, SOPStep step)
        {
            Actions.BeginFrame();
            int length = step.PassConditions.Where(c => c.Type == ConditionType.HandStable)
                .Select(c => c.StableFrames).DefaultIfEmpty(0).Max();
            if (length == 0) return;
            foreach (int id in Centers.Keys.Except(hands.Select(h => h.TrackId)).ToArray()) Centers.Remove(id);
            foreach (var hand in hands)
            {
                if (!Centers.TryGetValue(hand.TrackId, out var history)) Centers.Add(hand.TrackId, history = new());
                history.Enqueue(hand.GetCenter());
                if (history.Count > length) history.Dequeue();
            }
        }
    }

    public void Reset() => _windows.Clear();

    // Some backends assign the same left/right ID across people. Ambiguous IDs carry no temporal evidence.
    private static IEnumerable<HandPose> UniqueHands(HandPoseEstimationResult? result) =>
        (result?.Hands ?? Enumerable.Empty<HandPose>()).GroupBy(h => h.TrackId)
            .Where(g => g.Key >= 0 && g.Count() == 1).Select(g => g.First());

    private static IEnumerable<HandPose> MatchingHands(StepCondition condition, IEnumerable<HandPose> hands)
    {
        var side = condition.Parameters.GetValueOrDefault("HandSide", "right")?.ToString()?.ToLowerInvariant();
        return hands.Where(h => (side switch
        {
            "any" => true,
            "left" or "左手" => h.HandType == HandType.Left,
            "right" or "右手" => h.HandType == HandType.Right,
            _ => false
        }));
    }

    private ConditionCheckResult CheckHandCondition(StepCondition condition, HandPose hand, string cameraId, EvidenceWindow window)
    {
        var center = hand.GetCenter();

        switch (condition.Type)
        {
            case ConditionType.HandInRegion:
            {
                var zone = GetZoneDefinition(condition.ZoneId ?? "", cameraId);
                if (zone == null) return new ConditionCheckResult { IsMet = false, Message = $"未找到区域: {condition.ZoneId}" };
                var zoneRect = new SKRect(zone.X, zone.Y, zone.X + zone.Width, zone.Y + zone.Height);
                return zoneRect.Contains(center)
                    ? new ConditionCheckResult { IsMet = true, Message = $"{condition.ZoneId} 内检测到手部" }
                    : new ConditionCheckResult { IsMet = false, Message = $"手部未在区域 {condition.ZoneId} 内" };
            }
            case ConditionType.HandNotInRegion:
            {
                var zone = GetZoneDefinition(condition.ZoneId ?? "", cameraId);
                if (zone == null) return new ConditionCheckResult { IsMet = false, Message = $"未找到区域: {condition.ZoneId}" };
                var zoneRect = new SKRect(zone.X, zone.Y, zone.X + zone.Width, zone.Y + zone.Height);
                return !zoneRect.Contains(center)
                    ? new ConditionCheckResult { IsMet = true, Message = $"手部不在区域 {condition.ZoneId} 内" }
                    : new ConditionCheckResult { IsMet = false, Message = $"手部仍在区域 {condition.ZoneId} 内" };
            }
            case ConditionType.HandStable:
            {
                // 若指定了区域，要求手部中心必须位于该区域内（P0-5 修复：原 hand_stable 丢弃 region）
                if (!string.IsNullOrEmpty(condition.ZoneId))
                {
                    var zone = GetZoneDefinition(condition.ZoneId, cameraId);
                    if (zone == null)
                        return new ConditionCheckResult { IsMet = false, Message = $"未找到区域: {condition.ZoneId}" };
                    var zoneRect = new SKRect(zone.X, zone.Y, zone.X + zone.Width, zone.Y + zone.Height);
                    if (!zoneRect.Contains(center))
                        return new ConditionCheckResult { IsMet = false, Message = $"手部未位于区域 {condition.ZoneId} 内，稳定判定取消" };
                }

                if (!window.Centers.TryGetValue(hand.TrackId, out var track) || track.Count < condition.StableFrames)
                    return new ConditionCheckResult { IsMet = false, Message = $"手部稳定帧不足（已跟踪 {track?.Count ?? 0}/{condition.StableFrames}）" };

                var tolerance = condition.Parameters.GetValueOrDefault("Tolerance", 20f);
                float tol = tolerance is float f ? f : Convert.ToSingle(tolerance);

                var recent = track.TakeLast(condition.StableFrames).ToList();
                if (!string.IsNullOrEmpty(condition.ZoneId) && GetZoneDefinition(condition.ZoneId, cameraId) is { } region)
                {
                    var rect = new SKRect(region.X, region.Y, region.X + region.Width, region.Y + region.Height);
                    if (recent.Any(point => !rect.Contains(point))) return new() { Message = "等待手部在区域内持续稳定" };
                }
                double maxMove = 0;
                for (int i = 1; i < recent.Count; i++)
                {
                    var d = SKPoint.Distance(recent[i - 1], recent[i]);
                    if (d > maxMove) maxMove = d;
                }
                return maxMove <= tol
                    ? new ConditionCheckResult { IsMet = true, Message = $"手部已稳定（最大位移 {maxMove:F1}px ≤ {tol}px）" }
                    : new ConditionCheckResult { IsMet = false, Message = $"手部未稳定（最大位移 {maxMove:F1}px > {tol}px）" };
            }
            default:
                return new ConditionCheckResult { IsMet = false, Message = $"未支持的手部条件: {condition.Type}" };
        }
    }

    /// <summary>
    /// 手部靠近目标物体（手-物交互判断）：手的包围盒与目标检测框（外扩 margin）相交
    /// </summary>
    private ConditionCheckResult CheckHandNearObject(StepCondition condition, List<DetectedObject> detections, HandPose hand)
    {
        var marginValue = condition.Parameters.GetValueOrDefault("Margin", 30f);
        float margin = marginValue is float f ? f : Convert.ToSingle(marginValue);

        foreach (var det in detections)
        {
            if (!string.Equals(det.ClassName, condition.TargetObject, StringComparison.OrdinalIgnoreCase)) continue;
            if (det.Confidence < condition.MinConfidence) continue;

            var box = det.PixelBoundingBox;
            var expanded = new SKRect(box.Left - margin, box.Top - margin, box.Right + margin, box.Bottom + margin);
            if (expanded.IntersectsWith(hand.BoundingBox))
                return new ConditionCheckResult { IsMet = true, Message = $"手部靠近 '{condition.TargetObject}'" };
        }

        return new ConditionCheckResult { IsMet = false, Message = $"手部未靠近 '{condition.TargetObject}'" };
    }

    #endregion

    /// <summary>
    /// 检查目标是否存在
    /// </summary>
    private ConditionCheckResult CheckObjectPresent(StepCondition condition, List<DetectedObject> detections)
    {
        var matchingObjects = detections
            .Where(d => d.ClassName.Equals(condition.TargetObject, StringComparison.OrdinalIgnoreCase))
            .Where(d => d.Confidence >= condition.MinConfidence)
            .ToList();

        if (matchingObjects.Count == 0)
        {
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = $"未检测到目标 '{condition.TargetObject}'"
            };
        }

        var bestMatch = matchingObjects.OrderByDescending(d => d.Confidence).First();
        return new ConditionCheckResult
        {
            IsMet = true,
            Message = $"检测到 '{condition.TargetObject}'，置信度: {bestMatch.Confidence:P2}",
            MatchedObjects = matchingObjects
        };
    }

    /// <summary>
    /// 检查目标是否在指定区域内
    /// </summary>
    private ConditionCheckResult CheckObjectInZone(StepCondition condition, List<DetectedObject> detections, string cameraId)
    {
        if (string.IsNullOrEmpty(condition.ZoneId))
        {
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = "未指定区域ID"
            };
        }

        var zone = GetZoneDefinition(condition.ZoneId, cameraId);
        if (zone == null)
        {
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = $"未找到区域定义: {condition.ZoneId}"
            };
        }

        var matchingObjects = detections
            .Where(d => d.ClassName.Equals(condition.TargetObject, StringComparison.OrdinalIgnoreCase))
            .Where(d => d.Confidence >= condition.MinConfidence)
            .Where(d => zone.ContainsCenter(d.PixelBoundingBox))
            .ToList();

        if (matchingObjects.Count == 0)
        {
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = $"目标 '{condition.TargetObject}' 不在区域 '{condition.ZoneId}' 内"
            };
        }

        return new ConditionCheckResult
        {
            IsMet = true,
            Message = $"目标 '{condition.TargetObject}' 在区域 '{condition.ZoneId}' 内",
            MatchedObjects = matchingObjects
        };
    }

    /// <summary>
    /// 检查目标是否稳定
    /// </summary>
    private ConditionCheckResult CheckObjectStable(StepCondition condition, string cameraId, DateTime timestamp)
    {
        var trackedObjects = _stateMachine.TrackedObjects
            .Where(kv => kv.Value.CameraId.Equals(cameraId, StringComparison.OrdinalIgnoreCase))
            .Where(kv => kv.Value.LastUpdate == timestamp && kv.Value.ClassName.Equals(condition.TargetObject, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Value)
            .ToList();

        if (trackedObjects.Count == 0)
        {
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = $"未跟踪到目标 '{condition.TargetObject}'"
            };
        }

        var toleranceValue = condition.Parameters.GetValueOrDefault("Tolerance", 20f);
        float tolerance = toleranceValue is float f ? f : Convert.ToSingle(toleranceValue);
        var zone = GetZoneDefinition(condition.ZoneId ?? "", cameraId);
        if (!string.IsNullOrEmpty(condition.ZoneId) && zone == null) return new() { Message = $"未找到区域 {condition.ZoneId}" };
        var stableObjects = trackedObjects.Where(o => o.IsStable(tolerance, condition.StableFrames, condition.MinConfidence, zone)).ToList();

        if (stableObjects.Count == 0)
        {
            var maxFrames = trackedObjects.Max(o => o.FrameCount);
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = $"目标 '{condition.TargetObject}' 尚未稳定（已跟踪 {maxFrames} 帧，需要 {condition.StableFrames} 帧）"
            };
        }

        var mostStable = stableObjects.OrderByDescending(o => o.FrameCount).First();
        return new ConditionCheckResult
        {
            IsMet = true,
            Message = $"目标 '{condition.TargetObject}' 已稳定（跟踪 {mostStable.FrameCount} 帧）",
            MatchedObjects = new List<DetectedObject>() // 这里需要转换
        };
    }

    /// <summary>
    /// 检查目标是否不存在
    /// </summary>
    private ConditionCheckResult CheckObjectAbsent(StepCondition condition, List<DetectedObject> detections)
    {
        var matchingObjects = detections
            .Where(d => d.ClassName.Equals(condition.TargetObject, StringComparison.OrdinalIgnoreCase))
            .Where(d => d.Confidence >= condition.MinConfidence)
            .ToList();

        if (matchingObjects.Count > 0)
        {
            var bestMatch = matchingObjects.OrderByDescending(d => d.Confidence).First();
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = $"不应存在目标 '{condition.TargetObject}'，但检测到置信度 {bestMatch.Confidence:P2}"
            };
        }

        return new ConditionCheckResult
        {
            IsMet = true,
            Message = $"目标 '{condition.TargetObject}' 不存在，符合条件"
        };
    }

    /// <summary>
    /// 检查前置序列是否完成
    /// </summary>
    private ConditionCheckResult CheckSequenceComplete(StepCondition condition)
    {
        var completedSteps = _stateMachine.StepHistory.Select(r => r.StepId).ToHashSet();
        var requiredSteps = condition.Parameters.GetValueOrDefault("RequiredSteps", new List<int>()) as List<int> ?? new List<int>();

        var missingSteps = requiredSteps.Where(s => !completedSteps.Contains(s)).ToList();

        if (missingSteps.Count > 0)
        {
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = $"前置步骤未完成: {string.Join(", ", missingSteps)}"
            };
        }

        return new ConditionCheckResult
        {
            IsMet = true,
            Message = "所有前置步骤已完成"
        };
    }

    /// <summary>
    /// 检查时间是否满足
    /// </summary>
    private ConditionCheckResult CheckTimeElapsed(StepCondition condition, DateTime timestamp)
    {
        var elapsedSeconds = (timestamp - _stateMachine.StepStartTime).TotalSeconds;
        var requiredSecondsValue = condition.Parameters.GetValueOrDefault("RequiredSeconds", 0.0);
        double requiredSeconds = requiredSecondsValue is double d ? d : Convert.ToDouble(requiredSecondsValue);

        if (elapsedSeconds < requiredSeconds)
        {
            return new ConditionCheckResult
            {
                IsMet = false,
                Message = $"时间未满足，已等待 {elapsedSeconds:F1}s，需要 {requiredSeconds:F1}s"
            };
        }

        return new ConditionCheckResult
        {
            IsMet = true,
            Message = $"时间满足，已等待 {elapsedSeconds:F1}s"
        };
    }

    private ZoneDefinition? GetZoneDefinition(string zoneId, string cameraId)
        => _zones.GetValueOrDefault(ZoneKey(cameraId, zoneId));

    private static string ZoneKey(string cameraId, string zoneId) => $"{cameraId}:{zoneId}";

}

/// <summary>
/// 条件评估结果
/// </summary>
public class ConditionEvaluationResult
{
    public bool IsPass { get; set; }
    public string Message { get; set; } = "";
    public StepCondition? FailedCondition { get; set; }
    public List<ConditionCheckResult> CheckResults { get; set; } = new();
}

/// <summary>
/// 条件检查结果
/// </summary>
public class ConditionCheckResult
{
    public bool IsMet { get; set; }
    public bool WaitingForStability { get; set; }
    public string Message { get; set; } = "";
    public List<DetectedObject> MatchedObjects { get; set; } = new();
}


