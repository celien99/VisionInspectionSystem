using Core.Services;
using Modules.SOP.Models;
using SkiaSharp;

namespace Modules.SOP.Services;

// Each accepted frame extends exactly one hand/object trajectory or starts a new one at source.
internal sealed class HandObjectTrajectory
{
    private sealed record Sample(TrackedObject Object, int ContactFrames, bool AtDestination, int ReleasedFrames);
    private Dictionary<(StepCondition, int), Sample> _previous = new(), _current = new();

    public void BeginFrame()
    {
        (_previous, _current) = (_current, _previous);
        _current.Clear();
    }

    public ConditionCheckResult Observe(StepCondition condition, HandPose hand, IReadOnlyList<HandPose> hands, List<DetectedObject> detections,
        Func<DetectedObject, TrackedObject?> trackFor, ZoneDefinition? source, ZoneDefinition? destination)
    {
        string action = condition.Parameters.GetValueOrDefault("Action") as string ?? "move";
        bool requiresSource = action != "putdown" || !string.IsNullOrEmpty(condition.Parameters.GetValueOrDefault("FromRegion") as string);
        if ((requiresSource && source == null) || (action != "pickup" && destination == null))
            return new() { Message = "缺少本相机的动作区域" };
        var key = (condition, hand.TrackId);
        _previous.TryGetValue(key, out var prior);
        var targets = detections.Where(d => d.ClassName.Equals(condition.TargetObject, StringComparison.OrdinalIgnoreCase)
            && d.Confidence >= condition.MinConfidence).ToArray();
        var contacts = targets.Where(d => Touches(hand, d.PixelBoundingBox)).Take(2).ToArray();
        var required = Math.Max(2, condition.StableFrames);
        if (contacts.Length == 1 && trackFor(contacts[0]) is { } obj && !OtherHandTouches(obj.BoundingBox))
        {
            var center = hand.GetCenter();
            bool outsideSource = source == null || (!source.Contains(center) && !source.ContainsCenter(obj.BoundingBox));
            bool outsideDestination = destination == null || (!destination.Contains(center) && !destination.ContainsCenter(obj.BoundingBox));
            bool atStart = outsideDestination && (source == null || (source.Contains(center) && source.ContainsCenter(obj.BoundingBox)));
            bool atEnd = outsideSource && (destination == null || (destination.Contains(center) && destination.ContainsCenter(obj.BoundingBox)));
            bool continuing = prior?.Object == obj && prior.ReleasedFrames == 0;
            if (continuing || atStart)
            {
                int frames = continuing ? prior!.ContactFrames + 1 : 1;
                _current[key] = new(obj, frames, atEnd, 0);
                if (atEnd && frames >= required && action is "pickup" or "move")
                    return new() { IsMet = true, Message = $"已持续持有 {condition.TargetObject} 并到位" };
            }
        }
        else if (contacts.Length == 0 && action == "putdown" && prior is { AtDestination: true } && prior.ContactFrames >= 2)
        {
            // Only a visible, separated hand and the same part still in the destination prove release.
            var part = targets.FirstOrDefault(d => trackFor(d) == prior.Object);
            if (part != null && destination?.ContainsCenter(part.PixelBoundingBox) == true && !OtherHandTouches(part.PixelBoundingBox) &&
                !hand.BoundingBox.IsEmpty && !hand.BoundingBox.IntersectsWith(part.PixelBoundingBox) &&
                hand.Keypoints.Any(IsContactPoint))
            {
                var released = prior with { ReleasedFrames = prior.ReleasedFrames + 1 };
                _current[key] = released;
                bool settled = released.ReleasedFrames >= condition.StableFrames;
                return new() { IsMet = settled, WaitingForStability = !settled, Message = $"已松手，目标位留件 {released.ReleasedFrames}/{condition.StableFrames} 帧" };
            }
        }
        return new() { Message = $"等待 {condition.TargetObject} 的连续{(action == "putdown" ? "搬运和松手" : "持物移动")}证据" };

        bool OtherHandTouches(SKRect box) => hands.Any(other => !ReferenceEquals(other, hand) && Touches(other, box));
    }

    private static bool IsContactPoint(HandKeypoint p) => p.IsValid && p.Type is
        (HandKeypointType.ThumbTip or HandKeypointType.IndexFingerTip or HandKeypointType.MiddleFingerTip or
         HandKeypointType.RingFingerTip or HandKeypointType.PinkyTip or HandKeypointType.MiddleFingerMCP);

    private static bool Touches(HandPose hand, SKRect box) => !box.IsEmpty &&
        hand.Keypoints.Any(p => IsContactPoint(p) && box.Contains(p.X, p.Y));
}
