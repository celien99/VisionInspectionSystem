namespace VisionInspection.Core.Services;

/// <summary>
/// 逐帧日志限流器。
/// <para>
/// 现场"画面卡顿"的一个主要来源：SOP 判定 / 手部检测 / 相机帧回调里都在**每帧**调用
/// <c>File.AppendAllText</c>，每帧十几次 open/write/close（Windows 带杀软时单次 0.1~1ms）。
/// </para>
/// <para>
/// 这里只做一件事：**同一来源每秒最多放行 N 行**，其余直接丢弃（不排队、不落盘）。
/// 控制台/Debug 输出不受影响，需要全量日志时设环境变量 <c>VISION_VERBOSE=1</c>。
/// </para>
/// </summary>
public static class LogThrottle
{
    private static readonly Dictionary<string, (long Second, int Count)> State = new();
    private static readonly object StateLock = new();
    private static readonly bool Verbose = Environment.GetEnvironmentVariable("VISION_VERBOSE") == "1";

    /// <summary>本秒内是否还允许写一行。超限返回 false。</summary>
    public static bool Allow(string key, int perSecond = 10)
    {
        if (Verbose) return true;

        var second = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
        lock (StateLock)
        {
            if (!State.TryGetValue(key, out var s) || s.Second != second)
            {
                State[key] = (second, 1);
                return true;
            }

            if (s.Count >= perSecond) return false;

            State[key] = (second, s.Count + 1);
            return true;
        }
    }
}
