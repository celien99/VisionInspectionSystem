namespace VisionInspection.Core.Services;

/// <summary>
/// 全系统唯一的路径来源：目录布局 + 模型/配置查找。
/// <para>
/// 目标：所有"写死 e:\... / configs/... / models/..."的地方都改为从这里取，
/// 换机器、换安装目录、调试期从 bin 目录启动都不再需要改代码。
/// </para>
/// <para>
/// 目录约定（<see cref="Root"/> = 程序集目录，可用环境变量 <c>VISION_HOME</c> 覆盖）：
/// <code>
/// {Root}/configs   ← system.json / sop_config.json / sop/*.yaml
/// {Root}/models    ← *.onnx（部署时放这里）
/// {Root}/logs      ← 运行日志
/// {Root}/data      ← sqlite（用户、模型库）
/// </code>
/// 开发期程序从 <c>bin/Debug/net8.0-windows</c> 启动时，会向上最多 4 层找到仓库根再取同名子目录。
/// </para>
/// </summary>
public static class AppPaths
{
    /// <summary>程序根目录。优先环境变量 VISION_HOME（部署到非程序目录时使用）。</summary>
    public static string Root { get; } = ResolveRoot();

    public static string ConfigDir => Path.Combine(Root, "configs");
    public static string ModelsDir => Path.Combine(Root, "models");
    public static string LogsDir => Path.Combine(Root, "logs");
    public static string DataDir => Path.Combine(Root, "data");

    private static readonly string[] ModelSubdirs = { "models", "Models", "yolo_models" };

    private static string ResolveRoot()
    {
        var home = Environment.GetEnvironmentVariable("VISION_HOME");
        if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(home))
            return Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>配置文件绝对路径（不保证存在）。<paramref name="relative"/> 可为 "sop_config.json" 或 "sop/sop_phone_usage.yaml"。</summary>
    public static string Config(string relative)
    {
        var direct = Path.Combine(ConfigDir, relative);
        if (File.Exists(direct)) return direct;

        // 开发期：从 bin 目录向上找仓库根的 configs/
        for (var dir = new DirectoryInfo(Root); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "configs", relative);
            if (File.Exists(candidate)) return candidate;
        }
        return direct;
    }

    /// <summary>配置目录（优先存在的那个；找不到时返回约定目录）。</summary>
    public static string ResolveConfigDir()
    {
        if (Directory.Exists(ConfigDir)) return ConfigDir;
        for (var dir = new DirectoryInfo(Root); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "configs");
            if (Directory.Exists(candidate)) return candidate;
        }
        return ConfigDir;
    }

    /// <summary>日志文件绝对路径（目录不存在时创建）。</summary>
    public static string Log(string fileName)
    {
        try { Directory.CreateDirectory(LogsDir); } catch { /* 只读安装目录：退化为程序目录 */ }
        return Path.Combine(LogsDir, fileName);
    }

    /// <summary>
    /// 解析模型路径。绝对路径直接返回（存在才返回）；相对路径依次在
    /// 程序目录、根/models、根/yolo_models、以及向上 4 层的同名位置查找。
    /// 找不到返回 null（调用方负责记录日志并降级）。
    /// </summary>
    public static string? ResolveModel(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        if (Path.IsPathRooted(path))
            return File.Exists(path) || Directory.Exists(path) ? path : null;

        var fileName = Path.GetFileName(path);

        foreach (var root in SearchRoots())
        {
            foreach (var sub in ModelSubdirs)
            {
                var candidate = Path.Combine(root, sub, path);
                if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;

                // 配方里常写 "yolo_models/x.onnx"，但模型实际放在 models/ —— 按文件名再兜一次
                if (!string.IsNullOrEmpty(fileName) &&
                    !string.Equals(fileName, path, StringComparison.Ordinal))
                {
                    var byName = Path.Combine(root, sub, fileName);
                    if (File.Exists(byName)) return byName;
                }
            }

            var plain = Path.Combine(root, path);
            if (File.Exists(plain) || Directory.Exists(plain)) return plain;
        }

        return null;
    }

    /// <summary>按文件名/相对路径查文件，命中返回绝对路径。</summary>
    public static string? FindFile(string relative)
    {
        foreach (var root in SearchRoots())
        {
            var candidate = Path.Combine(root, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// 一行式环境自检：实际使用的配置目录、配方数量、模型目录。
    /// 启动时写进 logs/startup.log —— 排查"读不到配方/模型"时先看这一行。
    /// </summary>
    public static string Describe()
    {
        var configsDir = ResolveConfigDir();
        var sopDir = Path.Combine(configsDir, "sop");

        int recipes = 0;
        try
        {
            if (Directory.Exists(sopDir))
                recipes = Directory.GetFiles(sopDir, "*.y*ml").Length;
        }
        catch { /* 忽略 */ }

        var modelDirs = new List<string>();
        foreach (var sub in ModelSubdirs)
        {
            var dir = Path.Combine(Root, sub);
            try
            {
                if (Directory.Exists(dir))
                    modelDirs.Add($"{dir}(onnx={Directory.GetFiles(dir, "*.onnx").Length})");
            }
            catch { /* 忽略 */ }
        }

        return $"Root={Root} | configs={configsDir} | sop={sopDir} | recipes={recipes} | " +
               $"models=[{(modelDirs.Count > 0 ? string.Join(", ", modelDirs) : "无 models/ 目录")}] | " +
               $"VISION_HOME={(Environment.GetEnvironmentVariable("VISION_HOME") ?? "-")}";
    }

    /// <summary>查找外部可执行文件（如 ffmpeg）：程序目录 → 子目录 → PATH。</summary>
    public static string? FindExecutable(string exeName, params string[] subdirs)
    {
        var fileName = OperatingSystem.IsWindows() ? exeName + ".exe" : exeName;

        foreach (var root in SearchRoots())
        {
            foreach (var sub in subdirs)
            {
                var candidate = Path.Combine(root, sub, fileName);
                if (File.Exists(candidate)) return candidate;
            }
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    /// 程序根 + 向上 8 层。开发期从 bin/Debug/net8.0-windows 启动时，
    /// 仓库根在 6 层之上；多留两层兼容不同的输出布局。
    /// </summary>
    private static IEnumerable<string> SearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dir = new DirectoryInfo(Root);
        for (var depth = 0; dir != null && depth <= 8; depth++, dir = dir.Parent)
        {
            if (seen.Add(dir.FullName)) yield return dir.FullName;
        }

        var cwd = Directory.GetCurrentDirectory();
        if (seen.Add(cwd)) yield return cwd;
    }
}
