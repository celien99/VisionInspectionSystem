using System.Text.Json;

namespace VisionInspection.Core.Services;

/// <summary>
/// 配方（SOP YAML）的定位与"上次使用"记忆。
/// <para>
/// 原先每个界面各自拼 <c>configs/sop</c>、<c>AppDomain.BaseDirectory/configs/last_recipe.json</c>，
/// 从 bin 目录启动时写进输出目录、下次又读不到。这里统一走 <see cref="AppPaths"/>。
/// </para>
/// </summary>
public static class RecipeFile
{
    /// <summary>配方目录（configs/sop）。</summary>
    public static string SopDir => Path.Combine(AppPaths.ResolveConfigDir(), "sop");

    private static string PointerPath => Path.Combine(AppPaths.ResolveConfigDir(), "last_recipe.json");

    /// <summary>列出全部配方（.yaml/.yml），按文件名排序。</summary>
    public static List<string> ListAll()
    {
        var found = new List<string>();
        foreach (var dir in new[] { SopDir, AppPaths.ResolveConfigDir() })
        {
            if (!Directory.Exists(dir)) continue;
            found.AddRange(Directory.GetFiles(dir, "*.yaml"));
            found.AddRange(Directory.GetFiles(dir, "*.yml"));
        }

        return found.Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    /// <summary>读取上次使用的配方绝对路径；无记录或文件已删除时返回 null。</summary>
    public static string? LoadLast()
    {
        try
        {
            if (!File.Exists(PointerPath)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(PointerPath));
            if (!doc.RootElement.TryGetProperty("last_recipe", out var value)) return null;

            var stored = value.GetString();
            if (string.IsNullOrWhiteSpace(stored)) return null;

            var full = Path.IsPathRooted(stored) ? stored : Path.Combine(SopDir, stored);
            return File.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>记住本次使用的配方（存相对 configs/sop 的路径，换安装目录仍有效）。</summary>
    public static void SaveLast(string fullPath)
    {
        try
        {
            var stored = fullPath;
            var sopDir = SopDir.TrimEnd(Path.DirectorySeparatorChar);
            if (fullPath.StartsWith(sopDir, StringComparison.OrdinalIgnoreCase))
                stored = fullPath[(sopDir.Length + 1)..];

            Directory.CreateDirectory(Path.GetDirectoryName(PointerPath)!);
            File.WriteAllText(PointerPath,
                JsonSerializer.Serialize(new { last_recipe = stored.Replace('\\', '/') }));
        }
        catch
        {
            // 记忆失败不影响主流程
        }
    }
}
