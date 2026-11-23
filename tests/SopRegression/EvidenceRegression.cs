using Core.Services;
using Modules.SOP.Models;
using SkiaSharp;

internal static class EvidenceRegression
{
    private const string Prefix = """
        sop:
          regions:
            tray: {x1: 0, y1: 0, x2: 30, y2: 60}
            nest: {x1: 20, y1: 0, x2: 70, y2: 60}
          steps:
        """;

    private static SOPStateMachine Start(params string[] steps)
    {
        var wf = SOPYamlConverter.ParseYaml(Prefix + "\n" + string.Join("\n", steps.Select((step, i) => $"    - {{id: '{i+1}', {step}}}")));
        wf.Settings.AutoResetOnComplete = false;
        var m = new SOPStateMachine(wf.Regions);
        m.Start(wf);
        return m;
    }

    private static DetectedObject Part(int x, string label = "part") => new() { ClassName = label, Confidence = .95f,
        PixelBoundingBox = new SKRectI(x-10, 10, x+10, 30) };

    private static void Frame(SOPStateMachine m, int index, List<DetectedObject> objects, int? handX = null, int? secondHandX = null)
    {
        HandPoseEstimationResult? hands = null;
        foreach (var position in new[] { handX, secondHandX })
        {
            if (position is not int x) continue;
            hands ??= new();
            var hand = new HandPose { HandType = HandType.Right, TrackId = 4 + hands.Hands.Count, BoundingBox = new(x-5,15,x+5,25) };
            foreach (var type in Enum.GetValues<HandKeypointType>()) hand.Keypoints.Add(new(type,x,20,0,1));
            hands.Hands.Add(hand);
        }
        m.ProcessFrame(objects, hands, DateTime.UnixEpoch.AddMilliseconds(index*40));
    }

    public static void Run(Action<bool,string> check)
    {
        var inZone = Start("detection: {method: object_in_zone, region: nest, target_object: part, stable_frames: 1}");
        Frame(inZone,0,[Part(11)]);
        check(inZone.Outcome == null, "box edge intersection does not establish occupancy");
        Frame(inZone,1,[Part(20)]);
        check(inZone.Outcome == SopCycleOutcome.OK, "center crossing the ROI boundary establishes occupancy");

        var stable = Start("detection: {method: pose_stable, region: nest, target_object: part, stable_frames: 3, tolerance: 10}");
        for (int i=0; i<5; i++) Frame(stable,i,[Part(19)]);
        Frame(stable,5,[Part(21)]); Frame(stable,6,[Part(21)]);
        check(stable.Outcome == null, "outside history cannot satisfy an in-zone stability window");
        Frame(stable,7,[Part(21)]);
        check(stable.Outcome == SopCycleOutcome.OK, "full in-zone position window passes");

        var pickup = Start("detection: {method: hand_action, action: pickup, from_region: tray, target_object: part, stable_frames: 20}");
        for (int i=0; i<20; i++) Frame(pickup,i,[Part(12)],28);
        Frame(pickup,20,[Part(12)],12);
        check(pickup.Outcome == null, "proximity and stationary contact are insufficient for pickup");
        for (int i=1; i<20; i++) Frame(pickup,20+i,[Part(12+2*i)],12+2*i);
        check(pickup.Outcome == SopCycleOutcome.OK && pickup.LastCapture == DateTime.UnixEpoch.AddMilliseconds(39*40), "pickup requires departure and twenty consecutive contact frames");

        const string drop = "detection: {method: hand_action, action: putdown, to_region: nest, target_object: part, stable_frames: 2}";
        var remote = Start(drop);
        for (int i=0; i<15; i++) Frame(remote,i,[Part(8+3*i)],i==0 ? 8 : 95);
        check(remote.Outcome == null, "part moving alone cannot complete a placement");
        var hidden = Start(drop);
        for (int i=0; i<12; i++) Frame(hidden,i,[Part(8+3*i)],8+3*i);
        Frame(hidden,12,[Part(41)]);
        Frame(hidden,13,[Part(41)],100); Frame(hidden,14,[Part(41)],100);
        check(hidden.Outcome == null, "hand occlusion at arrival cannot count as release");
        var handedOff = Start(drop);
        for (int i=0; i<12; i++) Frame(handedOff,i,[Part(8+3*i)],8+3*i);
        Frame(handedOff,12,[Part(41)],100,41); Frame(handedOff,13,[Part(41)],100,41);
        check(handedOff.Outcome == null, "release into another visible hand cannot complete placement");
        var freeSource = Start(drop);
        for (int i=0; i<12; i++) Frame(freeSource,i,[Part(8+3*i)],8+3*i);
        Frame(freeSource,12,[Part(41)],100); Frame(freeSource,13,[Part(41)],100);
        check(freeSource.Outcome == SopCycleOutcome.OK, "placement without a named source observes carry from outside destination");

        const string marker = "detection: {method: object_present, target_object: marker, stable_frames: 1}";
        var skip = Start("detection: {method: time_elapsed, duration_ms: 0}",marker,
            "detection: {method: object_present, target_object: part, stable_frames: 15, duration_ms: 600}");
        Frame(skip,0,[]);
        for (int i=1; i<=15; i++) Frame(skip,i,[Part(40)]);
        check(skip.Violations.Count == 0, "skip requires the configured continuous dwell as well as frame count");
        Frame(skip,16,[Part(40)]);
        check(skip.Violations.Any(v=>v.Type==ViolationType.SkipStep), "fully observed out-of-order step raises a skip");
        Frame(skip,17,[Part(40,"marker")]); Frame(skip,18,[Part(40)]);
        check(skip.Outcome == null, "candidate dwell cannot advance the newly active step");
        var presence = Start(marker,marker);
        Frame(presence,0,[Part(40,"marker")]); Frame(presence,1,[Part(40,"marker")]);
        check(presence.Outcome == SopCycleOutcome.OK, "presence is a state and does not imply a new installation");

        const string slots = "timeout: 1, inspection_anchor: base, detection: {method: time_elapsed, duration_ms: 0, stable_frames: 1}, required_objects: [{object: base}, {object: part, zone: tray}, {object: part, zone: nest}]";
        var ambiguous = Start(slots);
        for (int i=0; i<=25; i++) Frame(ambiguous,i,[Part(5,"base"),Part(25)]);
        check(ambiguous.Outcome == SopCycleOutcome.Review, "one detection covering two slots requires review even with anchor");
        var reassigned = Start(slots);
        Frame(reassigned,0,[Part(5,"base"),Part(25),Part(10)]);
        check(reassigned.Outcome == SopCycleOutcome.OK, "unique complete assignment found even when greedy order would miss");
        var swapped = Start(slots);
        for (int i=0; i<=25; i++) Frame(swapped,i,[Part(5,"base"),Part(22),Part(26)]);
        check(swapped.Outcome == SopCycleOutcome.Review, "two interchangeable assignments cannot locate the parts");
    }
}
