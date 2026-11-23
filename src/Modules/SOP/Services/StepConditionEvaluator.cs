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
        string cameraId = "main_camera",
        bool ignoreFromRegion = false)
    {
        // 跳步检测（ViolationDetector.DetectSkipStep）会用同一实例评估"下一步"的意图，
        // 这类对非当前步骤的评估：① 不参与稳定帧门控（跳步是单帧意图判定）；
        // ② 不得清空/污染当前步骤的稳定帧计数——否则每帧互踩清零，计数永远达不到阈值
        //   （2026-09-29 回归修复：喝水配方步骤2卡死 + 跳工序不报警即由此引起）。
        bool isCurrentStep = !ignoreFromRegion && step.StepId == _stateMachine.CurrentStepId;

        if (isCurrentStep && step.StepId != _lastEvaluatedStepId)
        {
            _matches.Clear();
            _moves.Clear();
            foreach (var track in _handTracks.Values) track.CenterHistory.Clear();
            _lastEvaluatedStepId = step.StepId;
        }

        if (isCurrentStep && !ignoreFromRegion)
        {
            UpdateHandTracks(handResult, cameraId, step.PassConditions.Where(c => c.Type == ConditionType.HandStable).Select(c => c.StableFrames).DefaultIfEmpty(1).Max());
            ObserveCarriedObjects(detections, handResult, cameraId, timestamp);
            ObserveMoves(step, detections, handResult, cameraId);
        }

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
                foreach (var hand in MatchingHands(condition, handResult))
                {
                    var candidate = condition.Type == ConditionType.HandNearObject
                        ? CheckHandNearObject(condition, detections, hand)
                        : CheckHandCondition(condition, detections, hand, cameraId, timestamp);
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
                if (!isCurrentStep || !result.IsMet) return;
                // Stable / Move / Time have their own motion or temporal criteria.
                var required = condition.Type is ConditionType.HandStable or ConditionType.ObjectStable or
                    ConditionType.HandMoveFromTo or ConditionType.TimeElapsed or ConditionType.SequenceComplete ? 1 : condition.StableFrames;
                var hold = condition.Parameters.GetValueOrDefault("HoldMs") as int? ?? 0;
                if (required <= 1 && hold == 0) return;
                var key = (index, handId);
                var match = _matches.TryGetValue(key, out var prior) ? prior : (Since: timestamp, Frames: 0);
                nextMatches[key] = (match.Since, ++match.Frames);
                if (match.Frames < required || (timestamp - match.Since).TotalMilliseconds < hold)
                {
                    result.IsMet = false;
                    result.WaitingForStability = true;
                    result.Message += $"（连续 {match.Frames}/{required} 帧，要求保持 {hold} ms）";
                }
            }
        }
        if (isCurrentStep) _matches = nextMatches;
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

    /// <summary>
    /// 每只手最近若干帧的中心轨迹与经过区域记录（用于稳定/移动判断）
    /// </summary>
    private class HandTrackState
    {
        public List<SKPoint> CenterHistory { get; } = new();
        public System.Runtime.CompilerServices.ConditionalWeakTable<TrackedObject, Dictionary<StepCondition, DateTime>> Carried { get; } = new();
    }

    private readonly Dictionary<string, HandTrackState> _handTracks = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<(int Condition, int Hand), (DateTime Since, int Frames)> _matches = new();
    private int _lastEvaluatedStepId = -1;

    /// <summary>
    /// 清空稳定帧计数（工作流 Start/Reset 时调用）。
    /// 防止上一轮残留计数让新一轮的步骤"秒过"，绕过防抖。
    /// </summary>
    public void Reset()
    {
        _moves.Clear();
        _handTracks.Clear();
        _matches.Clear();
        _lastEvaluatedStepId = -1;
    }

    public void ResetStableCounts() => Reset();

    private void UpdateHandTracks(HandPoseEstimationResult? handResult, string cameraId, int historyLength)
    {
        var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hand in UniqueHands(handResult))
        {
            if (hand.TrackId < 0 || !hand.IsValidGesture(8)) continue;
            var trackKey = TrackKey(cameraId, hand.TrackId);
            alive.Add(trackKey);

            if (!_handTracks.TryGetValue(trackKey, out var track))
            {
                track = new HandTrackState();
                _handTracks[trackKey] = track;
            }

            var center = hand.GetCenter();
            track.CenterHistory.Add(center);
            if (track.CenterHistory.Count > historyLength)
                track.CenterHistory.RemoveAt(0);

        }

        // 清理离开画面的手
        foreach (var id in _handTracks.Keys.ToList())
        {
            if (id.StartsWith(cameraId + ":", StringComparison.OrdinalIgnoreCase) && !alive.Contains(id)) _handTracks.Remove(id);
        }
    }

    private void ObserveCarriedObjects(List<DetectedObject> detections, HandPoseEstimationResult? hands, string cameraId, DateTime timestamp)
    {
        if (hands == null || hands.Hands.Count == 0 || detections.Count == 0 || _stateMachine.Workflow == null) return;
        // Retain a qualified contact for each putdown condition. A later step can use the
        // pickup, but loss of either the hand track or the object track breaks the chain.
        foreach (var step in _stateMachine.Workflow.Steps.Where(s => s.StepId >= _stateMachine.CurrentStepId && s.CameraId.Equals(cameraId, StringComparison.OrdinalIgnoreCase)))
        foreach (var condition in step.PassConditions.Where(c => c.Type == ConditionType.HandMoveFromTo && !c.IsCarryMove && !string.IsNullOrEmpty(c.TargetObject)))
        {
            var to = condition.Parameters.GetValueOrDefault("ToRegion") as string;
            if (string.IsNullOrEmpty(to)) continue;
            var from = condition.Parameters.GetValueOrDefault("FromRegion") as string;
            var hasFrom = !string.IsNullOrEmpty(from);
            var zone = GetZoneDefinition(hasFrom ? from! : to, cameraId);
            if (zone == null) continue;
            foreach (var det in detections.Where(d => d.ClassName.Equals(condition.TargetObject, StringComparison.OrdinalIgnoreCase) && d.Confidence >= condition.MinConfidence))
            {
                if (IsInZone(det.PixelBoundingBox, zone) != hasFrom || _stateMachine.TrackFor(det) is not { } obj) continue;
                foreach (var hand in MatchingHands(condition, hands))
                    if (IsHandHoldingObject(hand, det) && _handTracks.TryGetValue(TrackKey(cameraId, hand.TrackId), out var track))
                        track.Carried.GetOrCreateValue(obj).TryAdd(condition, timestamp);
            }
        }
    }

    private sealed record MoveEvidence(TrackedObject Object, int Frames, bool Arrived);
    private Dictionary<(StepCondition, int Hand), MoveEvidence> _moves = new();

    // Rebuild from each fresh frame. An absent/ambiguous contact cannot survive to a later frame.
    private void ObserveMoves(SOPStep current, List<DetectedObject> detections, HandPoseEstimationResult? hands, string cameraId)
    {
        var previous = _moves;
        _moves = new();
        if (hands == null || hands.Hands.Count == 0 || detections.Count == 0) return;
        Observe(current);
        // Skip-step inspection reads independent evidence for the next step, on its own camera/model.
        if (_stateMachine.Workflow is not { Settings.EnableSkipDetection: true } workflow) return;
        var next = workflow.Steps.Where(s => s.StepId > current.StepId).MinBy(s => s.StepId);
        if (next != null && next.CameraId.Equals(cameraId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(next.ModelPath, current.ModelPath, StringComparison.OrdinalIgnoreCase)) Observe(next);

        void Observe(SOPStep step)
        {
            foreach (var condition in step.PassConditions.Where(c => c.IsCarryMove))
            {
                if (!_zones.TryGetValue(ZoneKey(cameraId, condition.Parameters.GetValueOrDefault("FromRegion") as string ?? ""), out var source) ||
                    !_zones.TryGetValue(ZoneKey(cameraId, condition.Parameters.GetValueOrDefault("ToRegion") as string ?? ""), out var destination)) continue;
                var from = new SKRect(source.X, source.Y, source.X + source.Width, source.Y + source.Height);
                var to = new SKRect(destination.X, destination.Y, destination.X + destination.Width, destination.Y + destination.Height);
                foreach (var hand in MatchingHands(condition, hands))
                {
                    var targets = detections.Where(d => d.ClassName.Equals(condition.TargetObject, StringComparison.OrdinalIgnoreCase) &&
                        d.Confidence >= condition.MinConfidence && Touches(hand, d)).Take(2).ToArray();
                    if (targets.Length != 1 || _stateMachine.TrackFor(targets[0]) is not { } obj) continue;
                    var key = (condition, hand.TrackId);
                    var center = hand.GetCenter();
                    var objectCenter = new SKPoint(obj.BoundingBox.MidX, obj.BoundingBox.MidY);
                    bool atStart = from.Contains(center) && from.Contains(objectCenter) && !to.Contains(center) && !to.Contains(objectCenter);
                    bool atEnd = to.Contains(center) && to.Contains(objectCenter) && !from.Contains(center) && !from.Contains(objectCenter);
                    if (previous.TryGetValue(key, out var old) && ReferenceEquals(obj, old.Object))
                    {
                        var frames = old.Frames + 1;
                        // Each center must actually leave A and enter B, even for partially overlapping zones.
                        bool arrived = atEnd && frames >= Math.Max(2, condition.StableFrames);
                        _moves[key] = new(obj, frames, arrived);
                    }
                    else if (atStart)
                        _moves[key] = new(obj, 1, false);
                }
            }
        }

        static bool Touches(HandPose hand, DetectedObject target)
        {
            SKRect box = target.PixelBoundingBox;
            return !box.IsEmpty && hand.Keypoints.Any(p => p.IsValid && p.Type is
                (HandKeypointType.ThumbTip or HandKeypointType.IndexFingerTip or HandKeypointType.MiddleFingerTip or
                 HandKeypointType.RingFingerTip or HandKeypointType.PinkyTip or HandKeypointType.MiddleFingerMCP) && box.Contains(p.X, p.Y));
        }
    }

    // Some backends assign the same left/right ID across people. Ambiguous IDs carry no temporal evidence.
    private static IEnumerable<HandPose> UniqueHands(HandPoseEstimationResult? result) =>
        (result?.Hands ?? Enumerable.Empty<HandPose>()).GroupBy(h => h.TrackId)
            .Where(g => g.Key >= 0 && g.Count() == 1).Select(g => g.First());

    private static IEnumerable<HandPose> MatchingHands(StepCondition condition, HandPoseEstimationResult? handResult)
    {
        var side = condition.Parameters.GetValueOrDefault("HandSide", "right")?.ToString()?.ToLowerInvariant();
        return UniqueHands(handResult).Where(h => h.IsValidGesture(8) && (side switch
        {
            "any" => true,
            "left" or "左手" => h.HandType == HandType.Left,
            "right" or "右手" => h.HandType == HandType.Right,
            _ => false
        }));
    }

    private ConditionCheckResult CheckHandCondition(StepCondition condition, List<DetectedObject> detections, HandPose hand, string cameraId, DateTime timestamp)
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

                if (!_handTracks.TryGetValue(TrackKey(cameraId, hand.TrackId), out var track) || track.CenterHistory.Count < condition.StableFrames)
                    return new ConditionCheckResult { IsMet = false, Message = $"手部稳定帧不足（已跟踪 {track?.CenterHistory.Count ?? 0}/{condition.StableFrames}）" };

                var tolerance = condition.Parameters.GetValueOrDefault("Tolerance", 20f);
                float tol = tolerance is float f ? f : Convert.ToSingle(tolerance);

                var recent = track.CenterHistory.TakeLast(condition.StableFrames).ToList();
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
            case ConditionType.HandMoveFromTo:
            {
                var fromRegion = condition.Parameters.GetValueOrDefault("FromRegion", "")?.ToString() ?? "";
                var toRegion = condition.Parameters.GetValueOrDefault("ToRegion", "")?.ToString() ?? "";
                bool hasFrom = !string.IsNullOrEmpty(fromRegion);
                bool hasTo = !string.IsNullOrEmpty(toRegion);

                var zoneFrom = hasFrom ? GetZoneDefinition(fromRegion, cameraId) : null;
                var zoneTo = hasTo ? GetZoneDefinition(toRegion, cameraId) : null;
                if (hasFrom && zoneFrom == null) return new ConditionCheckResult { IsMet = false, Message = $"未找到起始区域: {fromRegion}" };
                if (hasTo && zoneTo == null) return new ConditionCheckResult { IsMet = false, Message = $"未找到目标区域: {toRegion}" };

                if (condition.IsCarryMove)
                {
                    bool arrived = _moves.TryGetValue((condition, hand.TrackId), out var evidence) && evidence.Arrived;
                    return new() { IsMet = arrived, Message = arrived ? $"已将 {condition.TargetObject} 搬至 {toRegion}" : $"等待同一只手连续携带 {condition.TargetObject} 从 {fromRegion} 到 {toRegion}" };
                }
                foreach (var obj in detections.Where(d => d.ClassName.Equals(condition.TargetObject, StringComparison.OrdinalIgnoreCase) && d.Confidence >= condition.MinConfidence))
                {
                    bool holding = IsHandHoldingObject(hand, obj, out _);
                    if (!hasTo && holding && (!hasFrom || IsInZone(obj.PixelBoundingBox, zoneFrom!)))
                        return new ConditionCheckResult { IsMet = true, Message = "已在起始区域握住目标物料" };
                    if (!hasTo || !IsInZone(obj.PixelBoundingBox, zoneTo!)) continue;
                    var track = _stateMachine.TrackFor(obj);
                    if (track != null && _handTracks.TryGetValue(TrackKey(cameraId, hand.TrackId), out var handTrack) &&
                        handTrack.Carried.TryGetValue(track, out var evidence) && evidence.TryGetValue(condition, out var origin) && origin < timestamp)
                        return new ConditionCheckResult { IsMet = true, Message = "同一物料已由起点到达目标区" };
                }
                return new ConditionCheckResult { IsMet = false, Message = $"等待连续的手和物料轨迹：{condition.TargetObject}" };
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
    /// 判断手是否正拿着目标物体：手框与物体框相交，或手的中心落在物体框内。
    /// </summary>
    private static bool IsHandHoldingObject(HandPose hand, DetectedObject targetObj)
    {
        return IsHandHoldingObject(hand, targetObj, out _);
    }

    /// <summary>
    /// 重载：附加返回每条子判断的明细，供调试日志使用。
    /// </summary>
    private static bool IsHandHoldingObject(HandPose hand, DetectedObject targetObj, out string detail)
    {
        var objBox = targetObj.PixelBoundingBox;
        if (objBox.IsEmpty)
        {
            detail = "(物体 BBox 为空)";
            return false;
        }

        // 1. 手部包围盒与物体包围盒相交
        bool intersectBox = hand.BoundingBox.IntersectsWith(objBox);
        if (intersectBox)
        {
            detail = $"(BBox相交: hand[{hand.BoundingBox.Left:F0},{hand.BoundingBox.Top:F0}-{hand.BoundingBox.Right:F0},{hand.BoundingBox.Bottom:F0}] ∩ obj[{objBox.Left:F0},{objBox.Top:F0}-{objBox.Right:F0},{objBox.Bottom:F0}])";
            return true;
        }

        // 2. 手部中心落在物体框内
        var center = hand.GetCenter();
        bool centerInObj = objBox.Contains((int)center.X, (int)center.Y);
        if (centerInObj)
        {
            detail = $"(手中心({center.X:F0},{center.Y:F0})在物体内)";
            return true;
        }

        // 3. 手部中心在物体框附近（留 30px 容差）
        const float margin = 30f;
        var expanded = new SKRect(objBox.Left - margin, objBox.Top - margin, objBox.Right + margin, objBox.Bottom + margin);
        bool centerNearObj = expanded.Contains((int)center.X, (int)center.Y);
        if (centerNearObj)
        {
            detail = $"(手中心({center.X:F0},{center.Y:F0})在物体框外扩[{margin}px]范围内)";
            return true;
        }

        detail = $"(手中心({center.X:F0},{center.Y:F0})距离物体中心最近" +
                 $"|dx={center.X - (objBox.Left + objBox.Right) / 2:F0}|dy={center.Y - (objBox.Top + objBox.Bottom) / 2:F0}|)";
        return false;
    }

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
            .Where(d => IsInZone(d.PixelBoundingBox, zone))
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
        var stableObjects = trackedObjects.Where(o => o.IsStable(tolerance, condition.StableFrames, condition.MinConfidence)).ToList();

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
    {
        if (_zones.TryGetValue(ZoneKey(cameraId, zoneId), out var zone))
        {
            return zone;
        }

        var matches = _zones.Values
            .Where(candidate => candidate.ZoneId.Equals(zoneId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 1)
            return matches[0];

        Console.WriteLine($"[SOP] 警告: 未找到区域定义 '{zoneId}'，请检查 YAML 配置中的 regions 字段");
        return null;
    }

    private static string TrackKey(string cameraId, int trackId) => $"{cameraId}:{trackId}";
    private static string ObjectKey(string cameraId, string className) => $"{cameraId}:{className}";
    private static string ZoneKey(string cameraId, string zoneId) => $"{cameraId}:{zoneId}";

    private static bool IsInZone(SKRect objectBox, ZoneDefinition zone)
    {
        // 检查边界框是否与区域有重叠（IOU > 0 即可）
        var zoneBox = new SKRect(zone.X, zone.Y, zone.X + zone.Width, zone.Y + zone.Height);
        return objectBox.IntersectsWith(zoneBox);
    }
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


