using System.Reflection;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace Modules.SOP.Models;

internal static class SopRecipeValidation
{
    // Historical display/model metadata is extensible. Execution fields cannot be silently ignored.
    public static void Validate(SopyamlSop sop, string yaml, string path)
    {
        var document = new YamlStream();
        document.Load(new StringReader(yaml));
        var root = (YamlMappingNode)document.Documents[0].RootNode;
        var node = (YamlMappingNode)root.Children["sop"];
        if (sop.Steps.Count == 0 && sop.Monitoring?.Enabled != true && sop.CrossCamera?.Enabled != true)
            throw new InvalidOperationException($"{path}: steps 为空，且未启用区域或跨相机监控");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var numbers = new HashSet<int>();
        int previous = 0;
        for (int i = 0; i < sop.Steps.Count; i++)
        {
            var step = sop.Steps[i];
            var location = $"{path} steps[{i + 1}] '{step.Id}' ({step.Name})";
            void Require(bool valid, string error) { if (!valid) throw new InvalidOperationException($"{location}: {error}"); }
            var source = (YamlMappingNode)((YamlSequenceNode)node.Children["steps"]).Children[i];
            CheckKeys<SopyamlStep>(source, location);
            int number = int.TryParse(step.Id, out var parsed) ? parsed : i + 1;
            Require(number > 0 && numbers.Add(number) && (string.IsNullOrEmpty(step.Id) || ids.Add(step.Id)), "步骤 id 重复或无效");
            Require(number > previous, "数值步骤 ID 必须与排列顺序一致");
            previous = number;
            if (step.Transitions.Count > 0)
            {
                var next = sop.Steps[(i + 1) % sop.Steps.Count].Id;
                Require(step.Transitions.Count == 1 && step.Transitions[0] == next, "transitions 仅支持顺序下一步或末步回到起点");
            }
            Require(step.Timeout >= 0, "timeout 必须大于等于 0");
            Require(step.ViolationRules is not { Count: > 0 }, "violation_rules 尚无执行器，请使用 forbidden_objects、must_keep 或 required_objects");
            var d = step.Detection ?? throw new InvalidOperationException($"{location}: 缺少 detection");
            CheckKeys<SopyamlDetection>((YamlMappingNode)source.Children["detection"], location + ".detection");
            Require(d.StableFrames > 0 && d.DurationMs is not < 0 && d.MinConfidence is >= 0 and <= 1, "检测帧数、时长或置信度无效");
            var method = d.Method.ToLowerInvariant();
            if (method is "object_present" or "object_in_zone" or "pose_stable" or "hand_near_object" or "hand_action" or "hand_move")
                Require(!string.IsNullOrWhiteSpace(d.TargetObject), "缺少 target_object");
            if (method is "object_in_zone" or "hand_in_region" or "hand_not_in_region") Require(!string.IsNullOrWhiteSpace(d.Region), "缺少 region");
            if (method == "hand_move")
            {
                Require(!string.IsNullOrWhiteSpace(d.FromRegion) && !string.IsNullOrWhiteSpace(d.ToRegion), "hand_move 需要 from_region 和 to_region");
                Require(!string.Equals(d.FromRegion, d.ToRegion, StringComparison.OrdinalIgnoreCase), "hand_move 的起点和终点必须不同");
            }
            if (method == "hand_action") Require(d.Action is "pickup" or "putdown" && (d.Action != "putdown" || !string.IsNullOrWhiteSpace(d.ToRegion)), "hand_action 的 action 或 to_region 无效");
            if (d.Hand != null) Require(d.Hand.ToLowerInvariant() is "left" or "right" or "any" or "左手" or "右手", "hand 仅支持 left、right 或 any");
            var camera = string.IsNullOrWhiteSpace(step.Camera) ? "main_camera" : step.Camera.Trim();
            void CheckZone(string? id)
            {
                if (string.IsNullOrEmpty(id)) return;
                Require(sop.Regions != null && sop.Regions.TryGetValue(id, out var z) && z.X2 > z.X1 && z.Y2 > z.Y1 &&
                    string.Equals(string.IsNullOrWhiteSpace(z.Camera) ? "main_camera" : z.Camera.Trim(), camera, StringComparison.OrdinalIgnoreCase), $"区域 '{id}' 不存在、坐标无效或不属于相机 '{camera}'");
            }
            CheckZone(d.Region); CheckZone(d.FromRegion); CheckZone(d.ToRegion);
            if (source.Children.TryGetValue("required_objects", out var required))
            {
                Require(step.RequiredObjects is { Count: > 0 }, "required_objects 不能为空");
                foreach (var item in (YamlSequenceNode)required) CheckKeys<SopyamlRequiredObject>((YamlMappingNode)item, location + ".required_objects");
                var materials = new HashSet<(string Label, string Zone)>();
                foreach (var item in step.RequiredObjects!)
                {
                    Require(!string.IsNullOrWhiteSpace(item.Object) && item.MinConfidence is >= 0 and <= 1, "required_objects 的类别或置信度无效");
                    Require(materials.Add((item.Object.Trim().ToUpperInvariant(), item.Zone?.Trim().ToUpperInvariant() ?? "")), "同区域的 required_objects 物料重复，当前不支持按数量计件");
                    CheckZone(item.Zone);
                }
            }
            if (step.InspectionAnchor != null) Require(step.RequiredObjects?.Any(r => r.Object.Equals(step.InspectionAnchor, StringComparison.OrdinalIgnoreCase)) == true, "inspection_anchor 必须引用 required_objects 中的物料");
        }
    }

    private static void CheckKeys<T>(YamlMappingNode node, string location)
    {
        var keys = typeof(T).GetProperties().Select(p => p.GetCustomAttribute<YamlMemberAttribute>()?.Alias).ToHashSet();
        foreach (var key in node.Children.Keys)
            if (!keys.Contains(key.ToString())) throw new InvalidOperationException($"{location}: 未知字段 '{key}'");
    }
}
