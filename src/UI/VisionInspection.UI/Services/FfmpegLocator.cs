using System;
using System.Collections.Generic;
using System.IO;

namespace VisionInspection.UI.Services
{
    /// <summary>
    /// ffmpeg / ffprobe 可执行文件定位器。
    /// 搜索顺序：VISION_FFMPEG_DIR 环境变量 → 显式注入目录 → 逐级向上查找 &lt;dir&gt;/ffmpeg/bin 与 &lt;dir&gt; → 系统 PATH。
    /// 注意：不要用 Path.Combine(BaseDirectory,".."*N) 硬编码层数，改为逐级向上遍历（历史教训：少回退一层就漏）。
    /// </summary>
    public static class FfmpegLocator
    {
        public const string FfmpegExe = "ffmpeg.exe";
        public const string FfprobeExe = "ffprobe.exe";

        /// <summary>可选的显式路径（由配置或其它服务注入），优先使用</summary>
        public static string? OverrideDirectory { get; set; }

        public static string? FindFfmpeg() => Find(FfmpegExe);

        public static string? FindFfprobe() => Find(FfprobeExe);

        public static string? Find(string exeName)
        {
            try
            {
                // ① 显式配置：VISION_FFMPEG_DIR（README / 使用手册承诺支持）
                var envDir = Environment.GetEnvironmentVariable("VISION_FFMPEG_DIR");
                if (!string.IsNullOrWhiteSpace(envDir))
                {
                    var p = System.IO.Path.Combine(envDir.Trim(), exeName);
                    if (File.Exists(p)) return p;
                }

                // ② 代码注入的显式目录
                if (!string.IsNullOrWhiteSpace(OverrideDirectory))
                {
                    var p = System.IO.Path.Combine(OverrideDirectory!, exeName);
                    if (File.Exists(p)) return p;
                }

                // ③ 程序目录及其逐级上级目录的 ffmpeg/bin、bin、目录自身
                foreach (var dir in CandidateDirectories())
                {
                    var p = System.IO.Path.Combine(dir, exeName);
                    if (File.Exists(p)) return p;
                }
            }
            catch
            {
                // 路径异常忽略
            }

            // ④ 系统 PATH：用户已经装好 ffmpeg 并加入 PATH 的情况（文档承诺支持）
            return FindOnPath(exeName);
        }

        /// <summary>在系统 PATH 中查找可执行文件；找不到返回 null。</summary>
        private static string? FindOnPath(string exeName)
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var raw in pathVar.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var dir = raw.Trim().Trim('"');
                    if (dir.Length == 0) continue;

                    var candidate = System.IO.Path.Combine(dir, exeName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // 非法 PATH 项忽略
                }
            }

            return null;
        }

        /// <summary>校验 ffmpeg/ffprobe 是否都可用，返回错误信息</summary>
        public static bool EnsureAvailable(out string ffmpeg, out string ffprobe, out string error)
        {
            ffmpeg = FindFfmpeg() ?? "";
            ffprobe = FindFfprobe() ?? "";

            if (!File.Exists(ffmpeg))
            {
                error = $"未找到 {FfmpegExe}。三选一：\n" +
                        "  • 放到程序目录（或任意上级目录）的 ffmpeg\\bin\\ 下；\n" +
                        "  • 把 ffmpeg 目录加入系统 PATH；\n" +
                        "  • 设置环境变量 VISION_FFMPEG_DIR 指向含 ffmpeg.exe 的目录。";
                ffmpeg = "";
                return false;
            }

            if (!File.Exists(ffprobe))
            {
                error = $"未找到 {FfprobeExe}（一般与 ffmpeg.exe 在同一目录）。";
                return false;
            }

            error = "";
            return true;
        }

        private static IEnumerable<string> CandidateDirectories()
        {
            foreach (var root in SearchRoots())
            {
                foreach (var dir in WalkUp(root))
                {
                    yield return System.IO.Path.Combine(dir, "ffmpeg", "bin");
                    yield return dir;
                }
            }
        }

        // 注意：迭代器方法中不能在带 catch 的 try 块里 yield（CS1626），故先收集根路径再逐个遍历。
        private static IEnumerable<string> SearchRoots()
        {
            var roots = new List<string>();

            try
            {
                var bd = AppDomain.CurrentDomain.BaseDirectory;
                if (!string.IsNullOrEmpty(bd)) roots.Add(bd);
            }
            catch { }

            try
            {
                var cd = Directory.GetCurrentDirectory();
                if (!string.IsNullOrEmpty(cd)) roots.Add(cd);
            }
            catch { }

            try
            {
                var asmDir = System.IO.Path.GetDirectoryName(typeof(FfmpegLocator).Assembly.Location);
                if (!string.IsNullOrEmpty(asmDir)) roots.Add(asmDir);
            }
            catch { }

            return roots;
        }

        /// <summary>从 start 逐级向上返回目录（含自身）</summary>
        private static IEnumerable<string> WalkUp(string start)
        {
            var dir = start;
            var guard = 0;
            while (!string.IsNullOrEmpty(dir) && guard++ < 16)
            {
                yield return dir;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
        }
    }
}
