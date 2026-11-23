using SkiaSharp;
using Modules.SOP.Models;
using Core.Services;

namespace Modules.SOP.Services;

/// <summary>
/// 违规检测器
/// </summary>
public class ViolationDetector
{
    private readonly SOPStateMachine _stateMachine;
    private readonly StepConditionEvaluator _conditionEvaluator;
    private readonly Dictionary<string, DateTime> _violationCooldown = new();
    private readonly Dictionary<string, ZoneDefinition> _zones;

    public ViolationDetector(SOPStateMachine stateMachine, StepConditionEvaluator conditionEvaluator, IReadOnlyList<ZoneDefinition>? zones = null)
    {
        _stateMachine = stateMachine;
        _conditionEvaluator = conditionEvaluator;
        _zones = (zones ?? new List<ZoneDefinition>()).ToDictionary(z => ZoneKey(z.CameraId, z.ZoneId), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 动态更新区域定义
    /// </summary>
    public void UpdateZones(IReadOnlyList<ZoneDefinition> zones)
    {
        _zones.Clear();
        foreach (var zone in zones)
        {
            _zones[ZoneKey(zone.CameraId, zone.ZoneId)] = zone;
        }
    }

    /// <summary>
    /// 检测违规
    /// </summary>
    public List<ViolationRecord> DetectViolations(SOPStep currentStep, List<DetectedObject> detections, HandPoseEstimationResult? handResult, DateTime timestamp, string cameraId = "main_camera", bool checkSkip = true)
    {
        var violations = new List<ViolationRecord>();

        foreach (var rule in currentStep.ViolationRules)
        {
            var violation = rule.Type switch
            {
                ViolationType.Timeout => DetectTimeout(currentStep, timestamp),
                ViolationType.ForbiddenObject => DetectForbiddenObject(currentStep, detections, timestamp),
                ViolationType.ObjectRemoved => DetectObjectRemoved(currentStep, detections, timestamp, cameraId),
                ViolationType.ZoneIntrusion => DetectZoneIntrusion(currentStep, detections, timestamp, cameraId),
                ViolationType.WrongOrder => DetectWrongOrder(currentStep, timestamp),
                _ => null
            };

            if (violation != null && !IsOnCooldown(violation, timestamp))
            {
                violations.Add(violation);
                SetCooldown(violation, timestamp);
            }
        }

        // 检查全局超时
        if (_stateMachine.Workflow?.Settings.EnableTimeoutDetection == true)
        {
            var timeoutViolation = DetectTimeout(currentStep, timestamp);
            if (timeoutViolation != null && !IsOnCooldown(timeoutViolation, timestamp))
            {
                violations.Add(timeoutViolation);
                SetCooldown(timeoutViolation, timestamp);
            }
        }

        // 全局跳步检测：基于"后续步骤的完整条件是否已满足"判断，比单纯检测物体出现更可靠，
        // 避免把正常的拿/放过程误判为跳步。仅在 EnableSkipDetection 开启、且非第一步时启用。
        if (_stateMachine.Workflow?.Settings.EnableSkipDetection == true)
        {
            var minStepId = _stateMachine.Workflow.Steps.Count > 0
                ? _stateMachine.Workflow.Steps.Min(s => s.StepId)
                : 0;
            if (currentStep.StepId > minStepId)
            {
                var skip = DetectSkipStep(currentStep, detections, handResult, timestamp, cameraId);
                if (checkSkip && skip != null && !IsOnCooldown(skip, timestamp))
                {
                    violations.Add(skip);
                    SetCooldown(skip, timestamp);
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// 检测超时
    /// </summary>
    private ViolationRecord? DetectTimeout(SOPStep step, DateTime timestamp)
    {
        var elapsedSeconds = (timestamp - _stateMachine.StepStartTime).TotalSeconds;

        // TimeoutSec <= 0 表示该步骤不限制超时（"启用本步超时"未勾选）
        if (step.TimeoutSec > 0 && elapsedSeconds > step.TimeoutSec)
        {
            return new ViolationRecord
            {
                Timestamp = timestamp,
                Type = ViolationType.Timeout,
                Description = $"步骤 '{step.StepName}' 超时，已用时 {elapsedSeconds:F1}s，限制 {step.TimeoutSec}s",
                StepId = step.StepId,
                Evidence = $"Timeout_{step.StepId}_{timestamp:HHmmss}"
            };
        }

        return null;
    }

    /// <summary>
    /// 检测禁止对象
    /// </summary>
    private ViolationRecord? DetectForbiddenObject(SOPStep step, List<DetectedObject> detections, DateTime timestamp)
    {
        var forbiddenClasses = step.ViolationRules
            .Where(r => r.Type == ViolationType.ForbiddenObject)
            .SelectMany(r =>
            {
                var obj = r.Parameters.GetValueOrDefault("ForbiddenClasses", null);
                return obj is System.Collections.IEnumerable enumerable && !(obj is string)
                    ? enumerable.Cast<object>().Select(o => o?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s))
                    : Enumerable.Empty<string>();
            })
            .Distinct()
            .ToList();

        foreach (var forbiddenClass in forbiddenClasses)
        {
            var detected = detections
                .Where(d => d.ClassName.Equals(forbiddenClass, StringComparison.OrdinalIgnoreCase))
                .Where(d => d.Confidence >= 0.5f)
                .ToList();

            if (detected.Count > 0)
            {
                var bestMatch = detected.OrderByDescending(d => d.Confidence).First();
                return new ViolationRecord
                {
                    Timestamp = timestamp,
                    Type = ViolationType.ForbiddenObject,
                    Description = $"检测到禁止对象 '{forbiddenClass}'，置信度 {bestMatch.Confidence:P2}",
                    StepId = step.StepId,
                    Evidence = $"Forbidden_{forbiddenClass}_{timestamp:HHmmss}"
                };
            }
        }

        return null;
    }

    /// <summary>
    /// 检测对象被移除
    /// </summary>
    private ViolationRecord? DetectObjectRemoved(SOPStep step, List<DetectedObject> detections, DateTime timestamp, string cameraId)
    {
        var mustKeepClasses = step.ViolationRules
            .Where(r => r.Type == ViolationType.ObjectRemoved)
            .SelectMany(r =>
            {
                var obj = r.Parameters.GetValueOrDefault("MustKeepClasses", null);
                return obj is System.Collections.IEnumerable enumerable && !(obj is string)
                    ? enumerable.Cast<object>().Select(o => o?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s))
                    : Enumerable.Empty<string>();
            })
            .Distinct()
            .ToList();

        foreach (var mustKeepClass in mustKeepClasses)
        {
            // 只有本运行周期确实见过该类别，当前缺失才算被移除。
            var wasPresent = _stateMachine.HasSeenObject(mustKeepClass, cameraId);

            if (wasPresent)
            {
                var currentlyPresent = detections.Any(d =>
                    d.ClassName.Equals(mustKeepClass, StringComparison.OrdinalIgnoreCase) &&
                    d.Confidence >= 0.5f);

                if (!currentlyPresent)
                {
                    return new ViolationRecord
                    {
                        Timestamp = timestamp,
                        Type = ViolationType.ObjectRemoved,
                        Description = $"必须保持的对象 '{mustKeepClass}' 被移除",
                        StepId = step.StepId,
                        Evidence = $"Removed_{mustKeepClass}_{timestamp:HHmmss}"
                    };
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 检测区域入侵
    /// </summary>
    private ViolationRecord? DetectZoneIntrusion(SOPStep step, List<DetectedObject> detections, DateTime timestamp, string cameraId)
    {
        var zoneRules = step.ViolationRules.Where(r => r.Type == ViolationType.ZoneIntrusion).ToList();

        foreach (var rule in zoneRules)
        {
            var zoneId = rule.Parameters.GetValueOrDefault("ZoneId", "")?.ToString() ?? "";
            var forbiddenClassesObj = rule.Parameters.GetValueOrDefault("ForbiddenClasses", null);
            var forbiddenClasses = forbiddenClassesObj is System.Collections.IEnumerable enumerable && !(forbiddenClassesObj is string)
                ? enumerable.Cast<object>().Select(o => o?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList()
                : new List<string>();

            if (string.IsNullOrEmpty(zoneId))
                continue;

            var zone = GetZoneDefinition(zoneId, cameraId);
            if (zone == null)
                continue;

            foreach (var forbiddenClass in forbiddenClasses)
            {
                var intrusions = detections
                    .Where(d => d.ClassName.Equals(forbiddenClass, StringComparison.OrdinalIgnoreCase))
                    .Where(d => d.Confidence >= 0.5f)
                    .Where(d => IntersectsZone(d.PixelBoundingBox, zone))
                    .ToList();

                if (intrusions.Count > 0)
                {
                    return new ViolationRecord
                    {
                        Timestamp = timestamp,
                        Type = ViolationType.ZoneIntrusion,
                        Description = $"对象 '{forbiddenClass}' 进入禁区 '{zoneId}'",
                        StepId = step.StepId,
                        Evidence = $"Intrusion_{forbiddenClass}_{zoneId}_{timestamp:HHmmss}"
                    };
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 检测跳步：若"下一个"步骤（按 StepId 顺序）的条件已经满足，而当前步骤尚未通过，
    /// 说明当前步骤被跳过。只检查下一步，避免把更后面的 time_elapsed 等条件误报为跳步。
    /// </summary>
    private ViolationRecord? DetectSkipStep(SOPStep step, List<DetectedObject> detections, HandPoseEstimationResult? handResult, DateTime timestamp, string cameraId)
    {
        if (_stateMachine.Workflow?.Settings.EnableSkipDetection != true)
            return null;

        // 只检查"下一个"步骤：跳步=当前步骤未完成却做了下一步。
        var nextStep = _stateMachine.Workflow.Steps
            .Where(s => s.StepId > step.StepId)
            .OrderBy(s => s.StepId)
            .FirstOrDefault();

        if (nextStep == null || !nextStep.CameraId.Equals(cameraId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(nextStep.ModelPath, step.ModelPath, StringComparison.OrdinalIgnoreCase) || nextStep.RequiredMaterials.Count > 0) return null;

        // 排除纯时间条件步骤（如 complete 的 time_elapsed），否则只要等 1 秒就误报跳步
        if (nextStep.PassConditions.Count == 1 && nextStep.PassConditions[0].Type == ConditionType.TimeElapsed)
            return null;

        // Read the next step without advancing it. A carry requires its own observed hand/object trajectory.
        var eval = _conditionEvaluator.EvaluateConditions(nextStep, detections, handResult, timestamp, cameraId);
        if (eval.IsPass)
        {
            return new ViolationRecord
            {
                Timestamp = timestamp,
                Type = ViolationType.SkipStep,
                Description = $"可能跳过了步骤 '{step.StepName}'，直接满足下一步 '{nextStep.StepName}'",
                StepId = step.StepId,
                Evidence = $"Skip_{step.StepId}_{nextStep.StepId}_{timestamp:HHmmss}"
            };
        }

        return null;
    }

    /// <summary>
    /// 检测顺序错误
    /// </summary>
    private ViolationRecord? DetectWrongOrder(SOPStep step, DateTime timestamp)
    {
        if (step.RequiredPreviousSteps.Count == 0)
            return null;

        var completedSteps = _stateMachine.StepHistory.Select(h => h.StepId).ToHashSet();
        var missingSteps = step.RequiredPreviousSteps.Where(s => !completedSteps.Contains(s)).ToList();

        if (missingSteps.Count > 0)
        {
            return new ViolationRecord
            {
                Timestamp = timestamp,
                Type = ViolationType.WrongOrder,
                Description = $"步骤顺序错误，缺少前置步骤: {string.Join(", ", missingSteps)}",
                StepId = step.StepId,
                Evidence = $"WrongOrder_{step.StepId}_{string.Join("_", missingSteps)}_{timestamp:HHmmss}"
            };
        }

        return null;
    }

    private bool IsOnCooldown(ViolationRecord violation, DateTime timestamp)
    {
        var key = $"{violation.Type}_{violation.StepId}";
        if (_violationCooldown.TryGetValue(key, out var lastTime))
        {
            return (timestamp - lastTime).TotalSeconds < 3; // 3秒冷却期
        }
        return false;
    }

    private void SetCooldown(ViolationRecord violation, DateTime timestamp)
    {
        var key = $"{violation.Type}_{violation.StepId}";
        _violationCooldown[key] = timestamp;
    }

    public void Reset() => _violationCooldown.Clear();

    private ZoneDefinition? GetZoneDefinition(string zoneId, string cameraId)
        => _zones.GetValueOrDefault(ZoneKey(cameraId, zoneId));

    private static string ZoneKey(string cameraId, string zoneId) => $"{cameraId}:{zoneId}";

    private static bool IntersectsZone(SKRect objectBox, ZoneDefinition zone)
    {
        var zoneBox = new SKRect(zone.X, zone.Y, zone.X + zone.Width, zone.Y + zone.Height);
        return objectBox.IntersectsWith(zoneBox);
    }
}
