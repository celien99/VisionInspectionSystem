using Core.Models;
using Core.Services;
using Desktop.Services;
using Microsoft.Extensions.Configuration;
using Modules.SOP;
using Modules.SOP.Models;
using Modules.SOP.Services;
using Modules.SOP.Wizard;
using System.Text.Json;
using SkiaSharp;

// Run from the repository root: dotnet run --project tests/SopRegression
var directory = Path.GetFullPath("configs/sop");
var epoch = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
var all = new[] { "part_base", "part_gasket", "part_cover", "part_screw" };
var output = Console.Out;
int passed = 0;
Environment.SetEnvironmentVariable("VISION_HOME", AppContext.BaseDirectory);
void Check(bool ok, string name)
{
    if (!ok) throw new Exception("FAILED: " + name);
    output.WriteLine("PASS " + name);
    passed++;
}
void Reject(Action action, string fragment)
{
    try { action(); }
    catch (InvalidOperationException e) when (e.Message.Contains(fragment)) { Check(true, "reject " + fragment); return; }
    throw new Exception("Expected error containing " + fragment);
}
DetectedObject Obj(string name, int x = 400, int y = 330, int radius = 6) => new() {
    ClassName = name, Confidence = .95f, PixelBoundingBox = new SKRectI(x-radius, y-radius, x+radius, y+radius)
};
HandPoseEstimationResult Hand(int x, int y, HandType side = HandType.Right, int id = 1)
{
    var h = new HandPose { HandType = side, TrackId = id, BoundingBox = new SKRect(x-6,y-6,x+6,y+6) };
    for (int i=0; i<21; i++) h.Keypoints.Add(new HandKeypoint((HandKeypointType)i,x,y,0,1));
    return new() { Hands = new() { h } };
}
SOPWorkflow Load(string name) => SOPYamlConverter.LoadFromYaml(Path.Combine(directory, name + ".yaml"));
SOPStateMachine Start(SOPWorkflow f, int? index = null)
{
    if (index.HasValue) f.Steps = new() { f.Steps[index.Value] };
    f.Settings.AutoResetOnComplete = false;
    var m = new SOPStateMachine(f.Regions); m.Start(f); return m;
}
void Frame(SOPStateMachine m, int t, List<DetectedObject> dets, HandPoseEstimationResult? hand = null, string camera = "main_camera")
    => m.ProcessFrame(dets, hand, epoch.AddMilliseconds(t), camera);
SOPStateMachine Final(Func<int, string[]> input, int until = 4000)
{
    var f = Load("sop_steering_housing"); f.Steps.Last().TimeoutSec = 4;
    var m = Start(f, 6);
    for (int t=0; t<=until; t+=40) Frame(m,t,input(t).Select(s=>Obj(s)).ToList());
    return m;
}
Console.SetOut(TextWriter.Null);
foreach (string f in Directory.GetFiles(directory, "*.yaml").Where(f => !f.EndsWith("_data.yaml")))
    Check(SOPYamlConverter.LoadFromYaml(f) != null, "load " + Path.GetFileName(f));
var empty = Final(_=>Array.Empty<string>());
Check(empty.Outcome == SopCycleOutcome.Review && empty.StepHistory.Count == 0, "empty final cannot release");
Check(Final(t=>t<1200?Array.Empty<string>():all).StepHistory.Single().Duration.TotalMilliseconds == 3200, "hold begins at workpiece arrival");
Check(Final(t=>t==0?all[..^1]:all).Outcome == SopCycleOutcome.OK, "one-frame omission recovers");
var partial = Final(_=>all[..^1]);
Check(partial.Outcome == SopCycleOutcome.NG && partial.Violations.Single().Type == ViolationType.MissingRequiredObject && partial.OutcomeReason.Contains("part_screw"), "persistent missing screw with visible anchor");
Check(Final(_=>all[1..]).Outcome == SopCycleOutcome.Review, "absent anchor goes to review");
var full = Start(Load("sop_steering_housing"));
for (int t=0; t<6000 && full.Outcome == null; t+=40)
{
    var step=full.CurrentStepId;
    var dets=all.Take(Math.Min(4,step)).Select(s=>Obj(s)).ToList();
    if (step==5) dets.Add(Obj("torque_wrench"));
    if (step==6) dets.Add(Obj("assembly_ok"));
    Frame(full,t,dets);
}
Check(full.Outcome == SopCycleOutcome.OK && full.StepHistory.Count == 7, "all seven steps and positive final");
var negativeFlow = Load("sop_steering_housing");
negativeFlow.Steps.Last().TimeoutSec = 4;
var noProduct = Start(negativeFlow);
for (int t = 0; t <= 10000 && noProduct.Outcome == null; t += 40)
{
    int id = noProduct.CurrentStepId;
    var visible = id == 7 ? new List<DetectedObject>() : all.Take(id).Select(s => Obj(s)).ToList();
    if (id == 5) visible.Add(Obj("torque_wrench"));
    if (id == 6) visible.Add(Obj("assembly_ok"));
    Frame(noProduct, t, visible);
}
Check(noProduct.Outcome == SopCycleOutcome.Review && noProduct.StepHistory.Count == 6, "six completed steps cannot release an empty final");
var autoFlow = Load("sop_steering_housing");
autoFlow.Steps = new() { autoFlow.Steps.Last() };
autoFlow.Steps[0].TimeoutSec = 3;
autoFlow.Settings.AutoResetOnComplete = true;
autoFlow.Settings.ResetDelaySec = 0;
var auto = new SOPStateMachine(autoFlow.Regions);
SOPCompletedEventArgs? priorCycle = null;
auto.WorkflowCompleted += (_, ended) => priorCycle = ended;
auto.Start(autoFlow);
for (int t = 0; t <= 3000; t += 40) Frame(auto, t, all[..^1].Select(s => Obj(s)).ToList());
Check(priorCycle?.Outcome == SopCycleOutcome.NG && priorCycle.InterruptedStep?.Duration.TotalSeconds == 3 && auto.CycleId != priorCycle.CycleId && auto.CurrentState == SOPExecutionState.Running, "confirmed missing starts the next configured cycle and preserves its duration");
for (int t = 3040; t <= 6040; t += 40) Frame(auto, t, new());
Check(auto.Outcome == SopCycleOutcome.Review && auto.CurrentState == SOPExecutionState.Error, "review waits for operator even with auto reset enabled");
var outside = Start(negativeFlow, 6);
for (int t = 0; t <= 4000; t += 40)
    Frame(outside, t, all.Select(s => s == "part_screw" ? Obj(s, 0, 0) : Obj(s)).ToList());
Check(outside.Outcome == SopCycleOutcome.NG && outside.OutcomeReason.Contains("part_screw"), "part outside fixture cannot satisfy final inspection");
Check(Final(t => t < 2000 ? Array.Empty<string>() : all).Outcome == SopCycleOutcome.OK, "complete evidence at the deadline is accepted");
var intermittent = Final(t => (t / 400) % 2 == 0 ? all[..^1] : all);
Check(intermittent.Outcome == SopCycleOutcome.Review, "intermittent missing material cannot produce a confirmed NG");
var gap = Start(Load("sop_steering_housing"),6);
Frame(gap,0,all.Select(s=>Obj(s)).ToList()); Frame(gap,4000,all.Select(s=>Obj(s)).ToList());
Check(gap.Outcome == null, "gap cannot count as continuous material evidence");
string One(string d) => "sop:\n  steps:\n    - { id: '1', name: inspect, " + d + " }";
Reject(()=>SOPYamlConverter.ParseYaml(One("deteciton: { method: time_elapsed }")), "deteciton");
Reject(()=>SOPYamlConverter.ParseYaml(One("detection: { method: time_elapsed }, required_objets: []")), "required_objets");
Reject(()=>SOPYamlConverter.ParseYaml(One("detection: { method: time_elapsed }, violation_rules: [{type: wrong_posture}]")), "violation_rules");
Reject(()=>SOPYamlConverter.ParseYaml(One("detection: { method: object_in_zone, target_object: part, region: typo }")), "typo");
Reject(()=>SOPYamlConverter.ParseYaml(One("detection: { method: time_elapsed }")+"\n    - { id: '1', detection: { method: time_elapsed } }"),"id 重复");
var handMove = Start(Load("sop_hand_action_demo"),1);
var zones=handMove.Workflow!.Regions; var a=zones.Single(z=>z.ZoneId=="part_box"); var b=zones.Single(z=>z.ZoneId=="fixture");
Frame(handMove,0,new(),Hand((int)a.X+5,(int)a.Y+5)); Frame(handMove,40,new(),Hand((int)b.X+5,(int)b.Y+5));
Check(handMove.Outcome == null && handMove.StepHistory.Count == 0, "empty hand cannot complete hand_move");
var left = Start(Load("sop_hand_action_demo"),0);
for(int t=0;t<=1000;t+=40) Frame(left,t,new(),Hand((int)a.X+5,(int)a.Y+5,HandType.Left));
Check(left.Outcome == null, "right-hand condition rejects left hand");
var ear=Start(Load("sop_phone_usage"),2); var earZone=ear.Workflow!.Regions.Single(z=>z.ZoneId=="ear_region");
for(int t=0;t<1000;t+=40) Frame(ear,t,new(),Hand((int)earZone.X+5,(int)earZone.Y+5));
Check(ear.Outcome == null, "hand dwell below one second waits");
Frame(ear,1000,new(),Hand((int)earZone.X+5,(int)earZone.Y+5)); Check(ear.Outcome == SopCycleOutcome.OK,"hand dwell at one second passes");
var lost = Start(Load("sop_hand_action_demo"), 1);
Frame(lost, 0, new(), Hand((int)a.X+5, (int)a.Y+5));
Frame(lost, 40, new());
Frame(lost, 80, new(), Hand((int)b.X+5, (int)b.Y+5));
Check(lost.Outcome == null, "a reused hand ID after loss cannot inherit the movement origin");
var alternating = Start(Load("sop_phone_usage"), 2);
alternating.Workflow!.Steps[0].PassConditions[0].Parameters["HandSide"] = "any";
for (int t = 0; t <= 1200; t += 40)
    Frame(alternating, t, new(), Hand((int)earZone.X+5, (int)earZone.Y+5, id: (t / 40) % 2));
Check(alternating.Outcome == null, "alternating hands cannot accumulate dwell time");
for (int t = 1240; t <= 2280; t += 40)
{
    var pair = Hand(0, 0, id: 0);
    pair.Hands.AddRange(Hand((int)earZone.X+5, (int)earZone.Y+5, id: 1).Hands);
    Frame(alternating, t, new(), pair);
}
Check(alternating.Outcome == SopCycleOutcome.OK, "any hand may qualify even if listed second");
var longPose = Start(SOPYamlConverter.ParseYaml(One("detection: {method: pose_stable, target_object: part, stable_frames: 40, min_confidence: 0.8}")));
for (int i = 0; i < 79; i++)
{
    var part = Obj("part");
    if (i == 39) part.Confidence = .5f;
    Frame(longPose, i*40, new() { part });
}
Check(longPose.Outcome == null, "weak detection interrupts a long pose window");
Frame(longPose, 79*40, new() { Obj("part") });
Check(longPose.Outcome == SopCycleOutcome.OK, "pose stable can use more than 30 qualifying frames");
var pose=Start(SOPYamlConverter.ParseYaml(One("detection: {method: pose_stable, target_object: part, stable_frames: 6}")));
for(int i=0;i<5;i++) Frame(pose,40*i,new(){Obj("part")}); Check(pose.Outcome == null,"pose stable needs configured six frames");
Frame(pose,200,new(){Obj("part")}); Check(pose.Outcome == SopCycleOutcome.OK, "pose stable keeps its target");
string transfer="sop:\n  regions:\n    a: { x1: 0, y1: 0, x2: 18, y2: 40 }\n    b: { x1: 80, y1: 0, x2: 110, y2: 40 }\n  steps:\n    - { id: '1', detection: { method: hand_action, action: putdown, target_object: part, from_region: a, to_region: b } }";
// Unrelated station names and an arbitrary model label keep these tests independent of sample recipes.
var carryYaml = """
    sop:
      regions:
        feeder: { x1: 0, y1: 0, x2: 18, y2: 40 }
        nest: { x1: 80, y1: 0, x2: 110, y2: 40 }
      steps:
        - id: '1'
          detection: {method: hand_move, hand: any, target_object: spacer_x, from_region: feeder, to_region: nest, stable_frames: 3}
    """;
Reject(() => SOPYamlConverter.ParseYaml(carryYaml.Replace("target_object: spacer_x, ", "")), "target_object");
Reject(() => SOPYamlConverter.ParseYaml(carryYaml.Replace("to_region: nest", "to_region: feeder")), "起点和终点");
var built = SOPYamlConverter.ParseYaml(carryYaml);
Check(built.Steps[0].NeedsObjectModel && built.Steps[0].PassConditions[0].TargetObject == "spacer_x", "carry step requires its configured model label");
var broken = SOPYamlConverter.ParseYaml(carryYaml);
broken.Steps[0].PassConditions[0].TargetObject = "";
Reject(() => Start(broken), "target_object");
var savedMove = Path.Combine(AppContext.BaseDirectory, "carry-roundtrip.yaml");
SOPYamlConverter.SaveToYaml(built, savedMove);
Check(SOPYamlConverter.LoadFromYaml(savedMove).Steps[0].PassConditions[0].TargetObject == "spacer_x", "runtime recipe export preserves the carried label");
var wizard = new SopWizardInput { Name = "test", IncludePersonEntry = false, Regions = new() {
    ["feeder"] = new() { X2 = 18, Y2 = 40 }, ["nest"] = new() { X1 = 80, X2 = 110, Y2 = 40 }
} };
wizard.Actions.Add(new() { Template = SopActionCatalog.All.Single(t => t.Key == "hand_move"),
    Params = new() { ["target_object"] = "spacer_x", ["from_region"] = "feeder", ["to_region"] = "nest" } });
Check(SOPYamlConverter.ParseYaml(SopYamlGenerator.GenerateYaml(wizard)).Steps[0].PassConditions[0].TargetObject == "spacer_x", "wizard carries the selected material through YAML");
SOPStateMachine RunCarry(Func<int,List<DetectedObject>> objects, Func<int,HandPoseEstimationResult?>? hands = null)
{
    var machine = Start(SOPYamlConverter.ParseYaml(carryYaml));
    for (int x = 8; x <= 96; x += 4)
        Frame(machine, (x-8)*8, objects(x), hands == null ? Hand(x,18) : hands(x));
    return machine;
}
List<DetectedObject> Spacer(int x) => new() { Obj("spacer_x", x, 18, 10) };
Check(RunCarry(Spacer).Outcome == SopCycleOutcome.OK, "same hand moves the same arbitrary target from feeder to nest");
Check(RunCarry(_ => new()).Outcome == null, "full empty-hand route never passes");
Check(RunCarry(x => new(){Obj("other", x,18,10)}).Outcome == null, "carrying the wrong class never passes");
Check(RunCarry(_ => Spacer(8)).Outcome == null, "leaving the target behind never passes");
Check(RunCarry(_ => new(){Obj("spacer_x",8,18,10),Obj("spacer_x",88,18,10)}).Outcome == null, "two identical stationary parts cannot complete a carry");
Check(RunCarry(Spacer, x => Hand(x, x == 48 ? 80 : 18)).Outcome == null, "loss of contact mid-route invalidates a carry");
Check(RunCarry(Spacer, x => x == 48 ? null : Hand(x,18)).Outcome == null, "reappearing hand cannot reuse a pickup");
Check(RunCarry(Spacer, x => Hand(x,18,id: x < 48 ? 1 : 2)).Outcome == null, "handoff cannot splice two hand tracks");
Check(RunCarry(x => x == 48 ? new() : Spacer(x)).Outcome == null, "object reappearance cannot reuse a pickup");
Check(RunCarry(x => { var d = Spacer(x); if (x == 48) d[0].Confidence = .1f; return d; }).Outcome == null, "weak detection breaks the carry chain");
Check(RunCarry(x => { var d = Spacer(x); if (x == 48) d.Add(Obj("spacer_x", x+2,18,10)); return d; }).Outcome == null, "ambiguous contacted parts break the carry chain");
var skipYaml = carryYaml.Replace("    - id: '1'", "    - {id: '1', detection: {method: time_elapsed, duration_ms: 0}}\n    - {id: '2', detection: {method: object_present, target_object: marker, stable_frames: 1}}\n    - id: '3'");
var skipMove = Start(SOPYamlConverter.ParseYaml(skipYaml));
Frame(skipMove, 0, new());
for (int x = 8; x <= 96; x += 4) Frame(skipMove, x*8, new(), Hand(x,18));
Check(skipMove.CurrentStepId == 2 && skipMove.Violations.Count == 0, "empty hand at next step destination cannot trigger skip alarm");
for (int x = 8; x <= 96; x += 4) Frame(skipMove, 800+x*8, Spacer(x), Hand(x,18));
Check(skipMove.Violations.Any(v => v.Type == ViolationType.SkipStep), "out-of-order carry requires the same complete object evidence");
Frame(skipMove, 1600, new(){Obj("marker")});
Frame(skipMove, 1640, Spacer(90), Hand(90,18));
Check(skipMove.CurrentStepId == 3 && skipMove.Outcome == null, "next step cannot inherit carry evidence from earlier step");
var stationary=Start(SOPYamlConverter.ParseYaml(transfer));
for(int i=0;i<4;i++) Frame(stationary,i*40,new(){Obj("part",8,18),Obj("part",95,18)},Hand(500,500));
Check(stationary.Outcome == null,"two unmoved parts cannot substitute for a transfer");
var moved=Start(SOPYamlConverter.ParseYaml(transfer));
for(int x=8;x<95;x+=4) Frame(moved,(x-8)*8,new(){Obj("part",x,18,10)},Hand(x,18));
Check(moved.Outcome == null, "putdown requires release even after a complete carry");
for (int i = 0; i < 5; i++) Frame(moved, 720 + 40*i, new(){Obj("part",92,18,10)}, Hand(120,18));
Check(moved.Outcome == SopCycleOutcome.OK, "same part remains at destination after visible release");
EvidenceRegression.Run(Check);
var uncertainPickup = Start(SOPYamlConverter.ParseYaml(transfer));
for (int x = 8; x < 95; x += 4)
{
    var part = Obj("part", x, 18, 10);
    if (x <= 28) part.Confidence = .15f;
    Frame(uncertainPickup, (x-8)*8, new() { part }, Hand(x,18));
}
Check(uncertainPickup.Outcome == null, "weak source detection cannot authorize a putdown");
var overlap = Start(SOPYamlConverter.ParseYaml("sop:\n  steps:\n    - {id: '1', detection: {method: time_elapsed, duration_ms: 0}}\n    - {id: '2', detection: {method: object_present, target_object: a, stable_frames: 2}}\n    - {id: '3', detection: {method: object_present, target_object: b, stable_frames: 1}}"));
Frame(overlap, 0, new());
for (int t = 40; t <= 120; t += 40) Frame(overlap, t, new(){Obj("a"),Obj("b")});
Check(overlap.Outcome == SopCycleOutcome.OK, "current step accumulating evidence is not a skipped step");
var roundTrip = Path.Combine(AppContext.BaseDirectory, "roundtrip.yaml");
var cfg = SOPYamlConverter.LoadFullConfig(Path.Combine(directory, "sop_steering_housing.yaml"));
SOPYamlConverter.SaveFullConfig(roundTrip, cfg);
SOPYamlConverter.SaveRegions(roundTrip, cfg.Sop!.Regions!);
var reloaded = SOPYamlConverter.LoadFromYaml(roundTrip).Steps.Last();
Check(reloaded.InspectionAnchor == "part_base" && reloaded.RequiredMaterials.Count == 4 && reloaded.StandardDurationSec == 2, "material thresholds, anchor and dwell survive both recipe editors");
var original = File.ReadAllText(roundTrip);
cfg.Sop.Steps.Last().InspectionAnchor = "typo";
Reject(() => SOPYamlConverter.SaveFullConfig(roundTrip, cfg), "inspection_anchor");
Check(File.ReadAllText(roundTrip) == original, "invalid editor save preserves the valid recipe");
var cams=Start(SOPYamlConverter.ParseYaml("sop:\n  steps:\n    - {id: '1', detection: {method: time_elapsed, duration_ms: 0}}\n    - {id: '2', detection: {method: object_present, target_object: base, stable_frames: 1}}\n    - {id: '3', camera: cam_2, detection: {method: object_present, target_object: tool, stable_frames: 3}}"));
Frame(cams,0,new()); Frame(cams,40,new(){Obj("base"),Obj("tool")});
for(int i=0;i<3;i++) Frame(cams,80,new(){Obj("tool")},camera:"cam_2");
Check(cams.Outcome == null && cams.Violations.Count == 0,"cached frame counted once and no cross-camera skip");
Frame(cams,120,new(){Obj("tool")},camera:"cam_2");Frame(cams,160,new(){Obj("tool")},camera:"cam_2");
Check(cams.Outcome == SopCycleOutcome.OK,"three fresh source frames pass");
using var bitmap = new SKBitmap(8,8);
using (var done = new SemaphoreSlim(0))
{
    var ids = new List<int>();
    using var pipeline = new CameraFramePipeline(CameraManager.Instance,_=>{}, frames=>{ ids.Add(frames["cam_2"].FrameNumber); done.Release(); return Task.CompletedTask; });
    pipeline.StartInference();
    for(int i=0;i<3;i++){ pipeline.EnqueueInference(i==0?"cam_2":"main_camera",bitmap,epoch.AddMilliseconds(i*40)); if (!done.Wait(5000)) throw new TimeoutException(); }
    Check(ids.Count==3 && ids.Distinct().Count()==1,"camera pipeline preserves the captured frame number");
}
using var module = new SOPModule();
var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["SOPModule:ModelPath"]="missing-test-model.onnx",["SOPModule:UseGpu"]="false",["SOPModule:CameraTimeoutMs"]="400" }).Build();
await module.InitializeAsync(config);
Reject(()=>module.StartWorkflow(Load("sop_steering_housing")), "模型文件不存在");
var timed = SOPYamlConverter.ParseYaml(One("detection: {method: time_elapsed, duration_ms: 10000}")); timed.Settings.AutoResetOnComplete=false;
module.StartWorkflow(timed);
var pending = (SOPModuleResult)await module.ProcessAsync(new(){["main_camera"]=new(){Image=bitmap,Timestamp=epoch}});
Check(!pending.IsPass && pending.CurrentState == SOPExecutionState.Running,"unfinished step never advertises pass");
var terminal = new TaskCompletionSource<SOPCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
module.WorkflowCompleted += (_,e)=>terminal.TrySetResult(e);
var ended=await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check(ended.Outcome==SopCycleOutcome.Fault && ended.Reason.Contains("main_camera"),"watchdog faults when every camera stops");
module.StartWorkflow(timed);
await module.ProcessAsync(new(){["main_camera"]=new(){Image=bitmap,Timestamp=epoch}});
string cycle=module.StateMachine!.CycleId;
module.StopWorkflow();module.StopWorkflow();
var log=Path.Combine(AppContext.BaseDirectory,"sop_records","sop_run_2026-01-01.jsonl");
var records=File.ReadLines(log).Where(l=>l.Contains(cycle)).ToList();
Check(records.Count==1 && records[0].Contains("Aborted"),"interrupted cycle recorded once even with no completed steps");
var interrupted = JsonSerializer.Deserialize<SopCycleRecord>(records.Single())!;
Check(interrupted.Steps.Count == 0 && interrupted.InterruptedStep?.StepId == 1, "interrupted step duration is recorded separately");
var exported = new System.Text.StringBuilder();
SopRunRecorder.AppendCsvRows(exported, interrupted);
Check(exported.ToString().Contains(cycle) && exported.ToString().Contains("Unfinished"), "dashboard export includes first-step interruptions");
var csvPath = Path.ChangeExtension(log, ".csv");
Check(File.ReadLines(csvPath).Any(l => l.StartsWith($"\"{interrupted.CycleIndex}\",", StringComparison.Ordinal) && l.Contains("Aborted")), "daily CSV includes first-step interruptions");
var immediate = SOPYamlConverter.ParseYaml(One("detection: {method: time_elapsed, duration_ms: 0}"));
immediate.Settings.AutoResetOnComplete = false;
module.StartWorkflow(immediate);
await module.ProcessAsync(new(){["main_camera"]=new(){Image=bitmap,Timestamp=epoch}});
module.StopWorkflow();
var stopped = (SOPModuleResult)await module.ProcessAsync(new());
Check(!stopped.IsPass && stopped.Outcome == null && stopped.StepHistory.Count == 0, "idle polling after stop cannot return the previous OK");
if (args.Contains("--models"))
{
    var modelRoot = Path.GetFullPath("yolo_models/yolov8s.onnx");
    using var live = new SOPModule();
    await live.InitializeAsync(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
        ["SOPModule:ModelPath"] = "missing-default-test-model.onnx", ["SOPModule:UseGpu"] = "false",
        ["SOPModule:HandPoseEstimation:Enabled"] = "false"
    }).Build());
    var person = SOPYamlConverter.ParseYaml(One("detection: {method: person_present}"));
    person.Model = new SOPModelConfig { Path = modelRoot, UseGpu = false };
    live.StartWorkflow(person);
    using var blank = new SKBitmap(160,120);
    blank.Erase(SKColors.Black);
    var observed = (SOPModuleResult)await live.ProcessAsync(new(){["main_camera"]=new(){Image=blank,Timestamp=epoch}});
    Check(observed.Success && observed.PerCameraDetections.ContainsKey("main_camera") && observed.CurrentState == SOPExecutionState.Running && !observed.IsPass, "CPU ONNX inference on a blank frame cannot satisfy person_present");
    person.Steps[0].ModelPath = "missing-default-test-model.onnx";
    Reject(() => live.StartWorkflow(person), "模型不可用");
    person.Steps[0].ModelPath = null;
    person.Steps[0].PassConditions[0].TargetObject = "part_screw";
    Reject(() => live.StartWorkflow(person), "缺少类别");
    Check(live.CurrentWorkflow == null && live.StateMachine!.CurrentState == SOPExecutionState.Idle, "invalid class set cannot activate a recipe");
    var hands = new MediaPipeHandService();
    try
    {
        await hands.InitializeAsync(new() { ModelPath = Path.GetFullPath("models"), UseGpu = false, InferenceInterval = 3 });
        var actual = await hands.DetectHandsAsync(blank, requireFresh: true);
        Check(hands.IsInitialized && actual.Hands.Count == 0, "CPU palm ONNX empty result is a valid observation");
    }
    finally { await hands.ShutdownAsync(); }
}
output.WriteLine($"{passed} regression checks passed.");
