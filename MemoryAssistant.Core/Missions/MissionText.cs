namespace MemoryAssistant.Core.Missions;

/// <summary>任务文案的统一措辞（草稿与正式任务共用，避免"每 1440 分钟"这类机器话）。</summary>
public static class MissionText
{
    /// <summary>周期分钟数 → 人话（每 30 分钟 / 每小时 / 每天 / 每周 / 每月）。</summary>
    public static string Interval(int minutes) => minutes switch
    {
        >= 43200 when minutes % 43200 == 0 => minutes == 43200 ? "每月" : $"每 {minutes / 43200} 个月",
        >= 10080 when minutes % 10080 == 0 => minutes == 10080 ? "每周" : $"每 {minutes / 10080} 周",
        >= 1440 when minutes % 1440 == 0 => minutes == 1440 ? "每天" : $"每 {minutes / 1440} 天",
        >= 60 when minutes % 60 == 0 => minutes == 60 ? "每小时" : $"每 {minutes / 60} 小时",
        _ => $"每 {minutes} 分钟",
    };

    /// <summary>
    /// 周期 → 人话（秒级优先，V4.1）。秒级只用于"每 30 秒"这类高频巡检，
    /// 超过 1 分钟就仍按分钟/小时/天措辞，避免出现"每 300 秒"这种机器话。
    /// </summary>
    public static string Interval(int minutes, int seconds)
        => seconds > 0 && seconds < 60 ? $"每 {seconds} 秒" : Interval(minutes);
}
