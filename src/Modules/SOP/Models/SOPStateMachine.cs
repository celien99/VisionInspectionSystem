using Core.Services;
using System.Diagnostics;
using SkiaSharp;
using Core.Models;
using Modules.SOP.Services;

namespace Modules.SOP.Models;

/// <summary>
/// SOP 执行状态机
/// </summary>
public class SOPStateMachine
{
    private SOPWorkflow? _workflow;
    private Dictionary<string, TrackedObject> _trackedObjects = new();
    private readonly List<StepExecutionRecord> _stepHistory = new();
    private readonly List<ViolationRecord> _violations = new();
    private readonly StepConditionEvaluator _conditionEvaluator;
    private readonly ViolationDetector _violationDetector;
    private readonly HashSet<string> _seenObjects = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _autoResetCts;

    private readonly MaterialInspection _inspection = new();
    private readonly Dictionary<string, DateTime> _captureTimes = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly TimeSpan MaxEvidenceGap = TimeSpan.FromMilliseconds(500);
    public string CycleId { get; private set; } = "";
    public SopCycleOutcome? Outcome { get; private set; }
    public string OutcomeReason { get; private set; } = "";
    public DateTime LastCapture { get; private set; }
    public SOPExecutionState CurrentState { get; private set; } = SOPExecutionState.Idle;
    public int CurrentStepId { get; private set; } = 0;
    public DateTime StepStartTime { get; private set; }
    public SOPWorkflow? Workflow => _workflow;
    public SOPWorkflow? CurrentWorkflow => _workflow;
    public IReadOnlyList<StepExecutionRecord> StepHistory => _stepHistory.AsReadOnly();
    public IReadOnlyList<ViolationRecord> Violations => _violations.AsReadOnly();
    public IReadOnlyDictionary<string, TrackedObject> TrackedObjects => _trackedObjects;
    public int LastEvaluatedStepId { get; private set; }
    public string LastEvaluatedStepName { get; private set; } = "";

    /// <summary>
    /// 【黄金数据集录制专用】最近一帧的逐条件判定快照。
    /// 为旁路录制器提供 decisions.jsonl 的数据源，判定行为不受影响。
    /// </summary>
    public ConditionEvaluationResult? LastEvaluation { get; private set; }

    /// <summary>【黄金数据集录制专用】当前生效的区域定义（供录制器写 meta.json）</summary>
    public IReadOnlyDictionary<string, ZoneDefinition> Zones => _conditionEvaluator.Zones;

    public event EventHandler<StepChangedEventArgs>? StepChanged;
    public event EventHandler<ViolationEventArgs>? ViolationDetected;
    public event EventHandler<SOPCompletedEventArgs>? WorkflowCompleted;
    public event EventHandler<StateChangedEventArgs>? StateChanged;
    /// <summary>单个步骤完成事件；HasViolations 指示该步是否伴随违规，供界面显示。</summary>
    public event EventHandler<StepCompletedEventArgs>? StepCompleted;

    private readonly object _syncRoot;
    public SOPStateMachine(IReadOnlyList<ZoneDefinition>? zones = null, object? syncRoot = null)
    {
        _syncRoot = syncRoot ?? new object();
        _conditionEvaluator = new StepConditionEvaluator(this, zones);
        _violationDetector = new ViolationDetector(this, _conditionEvaluator, zones);
    }

    /// <summary>
    /// 更新区域定义（配置热切换时调用）
    /// </summary>
    public void UpdateZones(IReadOnlyList<ZoneDefinition> zones)
    {
        _conditionEvaluator.UpdateZones(zones);
        _violationDetector.UpdateZones(zones);
        _conditionEvaluator.Reset();
        _trackedObjects.Clear();
        _inspection.Reset();
    }

    public void Start(SOPWorkflow workflow)
    {
        CancelAutoReset();
        if (workflow.Steps.GroupBy(s => s.StepId).Any(g => g.Count() != 1) || workflow.Steps.Any(s => s.PassConditions.Count == 0))
            throw new InvalidOperationException("步骤 ID 重复或缺少通过条件");
        foreach (var step in workflow.Steps)
        foreach (var condition in step.PassConditions.Where(c => c.Type == ConditionType.HandMoveFromTo))
            if (string.IsNullOrWhiteSpace(condition.TargetObject))
                throw new InvalidOperationException($"步骤 {step.StepId} 的手物动作缺少 target_object");
        _workflow = workflow;
        _historyLength = workflow.Steps.SelectMany(step => step.PassConditions)
            .Where(c => c.Type == ConditionType.ObjectStable).Select(c => c.StableFrames).DefaultIfEmpty(1).Max();
        _currentTracks.Clear();
        _nextTrackId = 0;
        CycleId = Guid.NewGuid().ToString("N");
        Outcome = null;
        OutcomeReason = "";
        LastCapture = default;
        _captureTimes.Clear();
        _inspection.Reset();
        _stepHistory.Clear();
        _violations.Clear();
        _trackedObjects.Clear();
        _seenObjects.Clear();
        _conditionEvaluator.Reset();
        _violationDetector.Reset();
        // ⭐ 修复：起始步骤取工作流中最小的 StepId（配方步骤不一定从 1 开始，
        // 例如配置窗口重排后 id 可能是 2,3,4...；硬编码 1 会导致找不到步骤而卡在第一步）。
        var firstStep = workflow.Steps.OrderBy(s => s.StepId).FirstOrDefault();
        CurrentStepId = firstStep?.StepId ?? 1;
        StepStartTime = default;
        LastEvaluatedStepId = 0;
        LastEvaluatedStepName = "";
        LastEvaluation = null;
        ChangeState(SOPExecutionState.Running);
        StepChanged?.Invoke(this, new StepChangedEventArgs(0, CurrentStepId, workflow.Steps.FirstOrDefault(s => s.StepId == CurrentStepId)?.StepName ?? ""));
    }

    public void ProcessFrame(List<DetectedObject> detections, HandPoseEstimationResult? handResult, DateTime timestamp, string cameraId = "main_camera")
    {
        if (_workflow == null || CurrentState != SOPExecutionState.Running)
            return;

        var currentStep = _workflow.Steps.FirstOrDefault(s => s.StepId == CurrentStepId);
        if (currentStep == null || !currentStep.CameraId.Equals(cameraId, StringComparison.OrdinalIgnoreCase)) return;
        if (_captureTimes.TryGetValue(cameraId, out var last))
        {
            if (timestamp <= last) return;
            if (timestamp - last > MaxEvidenceGap)
            {
                _inspection.Reset();
                _conditionEvaluator.Reset();
                _trackedObjects.Clear();
            }
        }
        if (StepStartTime != default && timestamp < StepStartTime) return;
        _captureTimes[cameraId] = timestamp;
        LastCapture = timestamp;

        if (StepStartTime == default)
            StepStartTime = timestamp;

        // 更新对象跟踪
        UpdateTrackedObjects(detections, timestamp, cameraId);
        foreach (var detection in detections)
        {
            if (detection.Confidence >= 0.5f && !string.IsNullOrWhiteSpace(detection.ClassName))
                _seenObjects.Add(ObjectKey(cameraId, detection.ClassName));
        }

        bool materialReady = true;
        string? materialMessage = null;
        if (currentStep.RequiredMaterials.Count > 0)
        {
            var inspection = _inspection.Observe(currentStep, detections, _conditionEvaluator.Zones, timestamp);
            materialReady = inspection.Ready;
            materialMessage = inspection.Message;
            var deadline = TimeSpan.FromSeconds(currentStep.TimeoutSec);
            var elapsed = timestamp - StepStartTime;
            if (currentStep.TimeoutSec > 0 && (elapsed > deadline || elapsed == deadline && !materialReady))
            {
                if (inspection.ConfirmedMissing)
                    EndCycle(SopCycleOutcome.NG, inspection.Message, timestamp, ViolationType.MissingRequiredObject);
                else
                    EndCycle(SopCycleOutcome.Review, "终检超时，需要复核：" + inspection.Message, timestamp);
                return;
            }
        }

        // 评估步骤条件（物体 + 手部姿态）
        LastEvaluatedStepId = currentStep.StepId;
        LastEvaluatedStepName = currentStep.StepName;
        var evaluation = _conditionEvaluator.EvaluateConditions(currentStep, detections, handResult, timestamp, cameraId);

        if (!materialReady)
        {
            evaluation.IsPass = false;
            evaluation.Message = materialMessage!;
        }

        // 检测违规
        var violations = _violationDetector.DetectViolations(currentStep, detections, handResult, timestamp, cameraId,
            checkSkip: materialReady && evaluation.CheckResults.Any(r => !r.IsMet && !r.WaitingForStability));
        foreach (var violation in violations)
        {
            RecordViolation(violation);
            ViolationDetected?.Invoke(this, new ViolationEventArgs(violation));
        }

        // 黄金数据集录制：只做只读快照，不参与任何判定分支
        LastEvaluation = evaluation;

        if (evaluation.IsPass)
        {
            CompleteCurrentStep(timestamp);
        }
        else if (!string.IsNullOrEmpty(evaluation.Message))
        {
            // 仅打印一次当前条件不满足的原因（每帧一次），便于排查"卡在某一步"问题
            EmitFailureDebug(currentStep, evaluation, detections, handResult);
            Console.WriteLine($"[SOP] 步骤 '{currentStep.StepName}' 条件未满足: {evaluation.Message}");
        }
    }

    /// <summary>
    /// 失败时写一行调试到文件，帮助定位 hand_action 不通过的真实原因。
    /// 只在条件未通过时打印，频率 ≈ 一行/帧 × 步骤数，不会爆量。
    /// </summary>
    private static void EmitFailureDebug(SOPStep step, ConditionEvaluationResult evaluation, List<DetectedObject> detections, HandPoseEstimationResult? handResult)
    {
        if (!LogThrottle.Allow("sop_condition_failure", 4)) return;
        try
        {
            int handCount = handResult?.Hands?.Count ?? 0;
            string handSummary = handCount == 0
                ? "none"
                : string.Join("|", handResult!.Hands.Select(h =>
                    $"{h.HandType}(kx={h.Keypoints.Count},bbox={h.BoundingBox.Left:F0},{h.BoundingBox.Top:F0}-{h.BoundingBox.Right:F0},{h.BoundingBox.Bottom:F0})"));
            string detSummary = detections.Count == 0
                ? "none"
                : string.Join("|", detections.Select(d =>
                    $"{d.ClassName}({d.Confidence:F2})"));
            string condSummary = evaluation.FailedCondition == null
                ? "?"
                : $"{evaluation.FailedCondition.Type}(target='{evaluation.FailedCondition.TargetObject}',minConf={evaluation.FailedCondition.MinConfidence:F2},params=[{string.Join(",", evaluation.FailedCondition.Parameters.Select(kv => $"{kv.Key}={kv.Value}"))}])";
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [SOP-Fail] step='{step.StepName}' cond={condSummary} | reason={evaluation.Message} | dets={detSummary} | hands={handSummary}";
            Console.WriteLine(line);
            Debug.WriteLine(line);
            File.AppendAllText(AppPaths.Log("sop_module_debug.log"), line + Environment.NewLine);
        }
        catch { }
    }

    public void Reset()
    {
        CancelAutoReset();
        _workflow = null;
        Outcome = null;
        OutcomeReason = "";
        CycleId = "";
        LastCapture = default;
        _inspection.Reset();
        _captureTimes.Clear();
        _currentTracks.Clear();
        _stepHistory.Clear();
        _violations.Clear();
        _trackedObjects.Clear();
        _seenObjects.Clear();
        _conditionEvaluator.Reset();
        _violationDetector.Reset();
        CurrentStepId = 0;
        StepStartTime = default;
        LastEvaluatedStepId = 0;
        LastEvaluatedStepName = "";
        LastEvaluation = null;
        ChangeState(SOPExecutionState.Idle);
    }

    /// <summary>
    /// 停止工作流
    /// </summary>
    public void Stop() => Reset();

    private readonly Dictionary<DetectedObject, TrackedObject> _currentTracks = new();
    internal TrackedObject? TrackFor(DetectedObject detection) => _currentTracks.GetValueOrDefault(detection);
    private int _nextTrackId = 0;
    private int _historyLength = 1;

    public bool HasSeenObject(string className, string cameraId)
        => _seenObjects.Contains(ObjectKey(cameraId, className));

    private void UpdateTrackedObjects(List<DetectedObject> detections, DateTime timestamp, string cameraId)
    {
        _currentTracks.Clear();
        // Compare against the previous frame, before updating any boxes. An ambiguous
        // association starts a new track rather than extending someone else's evidence.
        var candidates = detections.Select(d => _trackedObjects.Where(pair =>
            pair.Value.CameraId.Equals(cameraId, StringComparison.OrdinalIgnoreCase) &&
            pair.Value.ClassName.Equals(d.ClassName, StringComparison.OrdinalIgnoreCase) &&
            CalculateIOU(pair.Value.BoundingBox, d.PixelBoundingBox) > 0.3f).Select(pair => pair.Key).ToArray()).ToArray();
        var claims = candidates.SelectMany(keys => keys).ToLookup(key => key);
        var fresh = new Dictionary<string, TrackedObject>();
        for (int i = 0; i < detections.Count; i++)
        {
            var d = detections[i];
            string key;
            TrackedObject track;
            if (candidates[i] is [var prior] && claims[prior].Count() == 1)
            {
                key = prior;
                track = _trackedObjects[key];
                track.Update(d.PixelBoundingBox, timestamp, d.Confidence);
            }
            else
            {
                key = $"{cameraId}_{d.ClassName}_{_nextTrackId++}";
                track = new(cameraId, d.ClassName, d.PixelBoundingBox, timestamp, _historyLength, d.Confidence);
            }
            fresh.Add(key, track);
            _currentTracks[d] = track;
        }
        _trackedObjects = fresh;
    }

    /// <summary>
    /// 计算两个矩形的 IOU（交并比）
    /// </summary>
    private static float CalculateIOU(SKRect a, SKRect b)
    {
        float intersectLeft = Math.Max(a.Left, b.Left);
        float intersectTop = Math.Max(a.Top, b.Top);
        float intersectRight = Math.Min(a.Right, b.Right);
        float intersectBottom = Math.Min(a.Bottom, b.Bottom);

        if (intersectRight <= intersectLeft || intersectBottom <= intersectTop)
            return 0f;

        float intersectArea = (intersectRight - intersectLeft) * (intersectBottom - intersectTop);
        float areaA = a.Width * a.Height;
        float areaB = b.Width * b.Height;
        float unionArea = areaA + areaB - intersectArea;

        return unionArea > 0 ? intersectArea / unionArea : 0f;
    }

    private void CompleteCurrentStep(DateTime timestamp)
    {
        if (_workflow == null) return;

        var currentStep = _workflow.Steps.FirstOrDefault(s => s.StepId == CurrentStepId);
        if (currentStep == null) return;

        var record = new StepExecutionRecord
        {
            StepId = CurrentStepId,
            StepName = currentStep.StepName,
            StartTime = StepStartTime,
            EndTime = timestamp,
            Duration = timestamp - StepStartTime,
            IsPass = !_violations.Any(v => v.StepId == CurrentStepId),
            StandardDurationSec = currentStep.StandardDurationSec
        };
        _stepHistory.Add(record);

        // ⭐ 该步是否伴随违规（供界面显示）
        var stepHadViolation = _violations.Any(v => v.StepId == CurrentStepId);
        StepCompleted?.Invoke(this, new StepCompletedEventArgs(currentStep, stepHadViolation));

        // 检查是否完成所有步骤
        if (CurrentStepId >= _workflow.Steps.Max(s => s.StepId))
        {
            EndCycle(_violations.Count == 0 ? SopCycleOutcome.OK : SopCycleOutcome.NG, "", timestamp);

        }
        else
        {
            _inspection.Reset();
            var previousStepId = CurrentStepId;
            CurrentStepId = _workflow.Steps.Where(s => s.StepId > CurrentStepId).Min(s => s.StepId);
            _conditionEvaluator.Reset();
            _trackedObjects.Clear();
            StepStartTime = timestamp;
            StepChanged?.Invoke(this, new StepChangedEventArgs(previousStepId, CurrentStepId, _workflow.Steps.FirstOrDefault(s => s.StepId == CurrentStepId)?.StepName ?? ""));
        }
    }

    internal void EndCycle(SopCycleOutcome outcome, string reason, DateTime timestamp, ViolationType? violation = null)
    {
        if (_workflow == null || CurrentState != SOPExecutionState.Running || Outcome.HasValue) return;
        Outcome = outcome;
        OutcomeReason = reason;
        if (violation is { } type)
        {
            var record = new ViolationRecord { StepId = CurrentStepId, Timestamp = timestamp, Type = type, Description = reason };
            RecordViolation(record);
            ViolationDetected?.Invoke(this, new ViolationEventArgs(record));
        }
        var start = _stepHistory.FirstOrDefault()?.StartTime ?? (StepStartTime == default ? timestamp : StepStartTime);
        ChangeState(outcome is SopCycleOutcome.OK or SopCycleOutcome.NG ? SOPExecutionState.Completed : SOPExecutionState.Error);
        var workflow = _workflow;
        var cycle = CycleId;
        var step = workflow.Steps.FirstOrDefault(s => s.StepId == CurrentStepId);
        var interruptedStart = StepStartTime == default ? timestamp : StepStartTime;
        var interrupted = step != null && _stepHistory.All(s => s.StepId != CurrentStepId) ? new StepExecutionRecord
        {
            StepId = step.StepId, StepName = step.StepName, StandardDurationSec = step.StandardDurationSec,
            StartTime = interruptedStart, EndTime = timestamp < interruptedStart ? interruptedStart : timestamp,
            Duration = timestamp > interruptedStart ? timestamp - interruptedStart : TimeSpan.Zero
        } : null;
        WorkflowCompleted?.Invoke(this, new SOPCompletedEventArgs(_stepHistory, _violations)
        {
            WorkflowId = _workflow.Id, WorkflowName = _workflow.Name, CycleId = CycleId, Outcome = outcome,
            InterruptedStep = interrupted, Reason = reason, FinalStepId = CurrentStepId, StartTime = start, EndTime = timestamp < start ? start : timestamp
        });
        if (CurrentState == SOPExecutionState.Completed && workflow.Settings.AutoResetOnComplete && CycleId == cycle)
        {
            var resetCts = new CancellationTokenSource();
            _autoResetCts = resetCts;
            _ = AutoResetAsync(workflow, resetCts);
        }
    }

    private void RecordViolation(ViolationRecord violation)
    {
        _violations.Add(violation);
    }

    private void ChangeState(SOPExecutionState newState)
    {
        var oldState = CurrentState;
        CurrentState = newState;
        StateChanged?.Invoke(this, new StateChangedEventArgs(oldState, newState));
    }

    private async Task AutoResetAsync(SOPWorkflow workflow, CancellationTokenSource resetCts)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(workflow.Settings.ResetDelaySec), resetCts.Token);
            lock (_syncRoot)
            {
                if (resetCts.IsCancellationRequested || !ReferenceEquals(_autoResetCts, resetCts) ||
                    CurrentState != SOPExecutionState.Completed || !ReferenceEquals(_workflow, workflow)) return;
                Reset();
                Start(workflow);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_autoResetCts, resetCts))
            {
                _autoResetCts = null;
                resetCts.Dispose();
            }
        }
    }

    private void CancelAutoReset()
    {
        var cts = _autoResetCts;
        _autoResetCts = null;
        cts?.Cancel();
        cts?.Dispose();
    }

    private static string ObjectKey(string cameraId, string className) =>
        $"{cameraId}:{className}";
}

/// <summary>
/// SOP 执行状态
/// </summary>
public enum SopCycleOutcome { OK, NG, Review, Fault, Aborted }

public enum SOPExecutionState
{
    Idle,
    Running,
    Paused,
    Completed,
    Error
}

/// <summary>
/// 跟踪对象
/// </summary>
public class TrackedObject
{
    public string CameraId { get; }
    public string ClassName { get; }
    public SKRect BoundingBox { get; private set; }
    public DateTime FirstSeen { get; }
    public DateTime LastUpdate { get; private set; }
    public int FrameCount { get; private set; }
    public List<SKRect> PositionHistory { get; } = new();

    private readonly int _historyLength;
    private readonly List<double> _confidences = new();

    public TrackedObject(string cameraId, string className, SKRect boundingBox, DateTime timestamp, int historyLength = 30, double confidence = 1)
    {
        CameraId = cameraId;
        ClassName = className;
        BoundingBox = boundingBox;
        FirstSeen = timestamp;
        LastUpdate = timestamp;
        _historyLength = Math.Max(1, historyLength);
        Update(boundingBox, timestamp, confidence);
    }

    public void Update(SKRect newBox, DateTime timestamp, double confidence = 1)
    {
        BoundingBox = newBox;
        LastUpdate = timestamp;
        FrameCount++;
        PositionHistory.Add(newBox);
        _confidences.Add(confidence);
        if (PositionHistory.Count > _historyLength)
        {
            PositionHistory.RemoveAt(0);
            _confidences.RemoveAt(0);
        }
    }

    public bool IsStable(float tolerance, int frames = 5, double minConfidence = 0, ZoneDefinition? zone = null)
    {
        if (PositionHistory.Count < frames || _confidences.TakeLast(frames).Any(c => c < minConfidence)) return false;
        var recent = PositionHistory.TakeLast(frames).ToList();
        var centerX = recent.Average(r => r.MidX);
        var centerY = recent.Average(r => r.MidY);
        return recent.All(r => (zone == null || zone.ContainsCenter(r)) && Math.Abs(r.MidX - centerX) < tolerance && Math.Abs(r.MidY - centerY) < tolerance);
    }
}

    /// <summary>
    /// 步骤执行记录
    /// </summary>
    public class StepExecutionRecord
    {
        public int StepId { get; set; }
        public string StepName { get; set; } = "";
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public bool IsPass { get; set; }
        /// <summary>该步骤的标准耗时（秒），来自配方 standard_duration_ms（缺省回退 duration_ms）。</summary>
        public double StandardDurationSec { get; set; } = 0;
        public List<ViolationRecord> Violations { get; set; } = new();
    }

/// <summary>
/// 违规记录
/// </summary>
public class ViolationRecord
{
    public DateTime Timestamp { get; set; }
    public ViolationType Type { get; set; }
    public string Description { get; set; } = "";
    public int StepId { get; set; }
    public string Evidence { get; set; } = "";
}

// 事件参数类
public class StepChangedEventArgs : EventArgs
{
    public int PreviousStepId { get; }
    public int CurrentStepId { get; }
    public string StepName { get; }

    public StepChangedEventArgs(int previousStepId, int currentStepId, string stepName)
    {
        PreviousStepId = previousStepId;
        CurrentStepId = currentStepId;
        StepName = stepName;
    }
}

public class ViolationEventArgs : EventArgs
{
    public ViolationRecord Violation { get; }

    public ViolationEventArgs(ViolationRecord violation)
    {
        Violation = violation;
    }
}

    public class SOPCompletedEventArgs : EventArgs
    {
        public IReadOnlyList<StepExecutionRecord> StepHistory { get; }
        public IReadOnlyList<ViolationRecord> Violations { get; }
        public DateTime StartTime { get; init; }
        public DateTime EndTime { get; init; }
        public string CycleId { get; init; } = "";
        public SopCycleOutcome Outcome { get; init; }
        public string Reason { get; init; } = "";
        public int FinalStepId { get; init; }
        public StepExecutionRecord? InterruptedStep { get; init; }
        public TimeSpan TotalDuration => EndTime - StartTime;
        public bool HasViolations => Violations.Count > 0;

        /// <summary>本次运行所属的 SOP 流程名称（来自 SOPWorkflow.Name），供运行记录器归类。</summary>
        public string WorkflowName { get; set; } = "";
        /// <summary>本次运行所属的 SOP 流程 Id（来自 SOPWorkflow.Id）。</summary>
        public string WorkflowId { get; set; } = "";

        /// <summary>本次运行的操作人（登录用户名），由 SOPModule 在触发事件前注入，供运行记录器落盘与看板按用户过滤。</summary>
        public string Operator { get; set; } = "";

        public SOPCompletedEventArgs(IReadOnlyList<StepExecutionRecord> stepHistory, IReadOnlyList<ViolationRecord> violations)
        {
            // UI 异步读取事件；自动复位不能清空已完成周期的数据。
            StepHistory = stepHistory.ToArray();
            Violations = violations.ToArray();
        }
    }

public class StateChangedEventArgs : EventArgs
{
    public SOPExecutionState OldState { get; }
    public SOPExecutionState NewState { get; }

    public StateChangedEventArgs(SOPExecutionState oldState, SOPExecutionState newState)
    {
        OldState = oldState;
        NewState = newState;
    }
}

/// <summary>
/// 单个步骤完成事件参数
/// </summary>
public class StepCompletedEventArgs : EventArgs
{
    public SOPStep Step { get; }
    public bool HasViolations { get; }

    public StepCompletedEventArgs(SOPStep step, bool hasViolations)
    {
        Step = step;
        HasViolations = hasViolations;
    }
}
