using System.Text.RegularExpressions;
using MemoryAssistant.Core.Missions;

namespace MemoryAssistant.App.Display;

/// <summary>「最近动态」里的一条（首页用）。</summary>
public sealed record ActivityItem(string TimeText, string Kind, string Title, string Detail);

/// <summary>
/// 最近动态聚合：把两个**真实**来源合成一条时间线——
/// 本次会话发生过的工具调用（<see cref="ToolCallLog"/>）与后台任务的执行记录（调度器日志）。
///
/// 为什么不用一个假的"活动流"：首页要让老师看到"它刚才真的干了什么"。
/// 这两个来源都是运行时真实产生的，读不到就显示空（不编造）。
/// </summary>
public static class ActivityFeed
{
    private static readonly Regex LogLine = new(@"^\[(?<t>\d{2}:\d{2}:\d{2})\]\s*(?<body>.+)$", RegexOptions.Compiled);

    public static IReadOnlyList<ActivityItem> Build(ToolCallLog tools, MissionScheduler? scheduler, int take = 8)
    {
        var items = new List<(DateTimeOffset At, ActivityItem Item)>();

        foreach (var r in tools.Recent)
        {
            var title = r.Success ? $"调用工具 {r.Name}" : $"调用工具 {r.Name} 失败";
            var detail = r.Result.Length > 0 ? r.Result : r.Headline;
            items.Add((r.At, new ActivityItem(r.TimeText, "工具", title, Truncate(detail, 90))));
        }

        if (scheduler is not null)
        {
            var today = DateTimeOffset.Now.Date;
            foreach (var line in scheduler.Log)
            {
                var m = LogLine.Match(line);
                if (!m.Success) continue;
                var t = TimeSpan.Parse(m.Groups["t"].Value);
                var at = new DateTimeOffset(today.Add(t), DateTimeOffset.Now.Offset);
                items.Add((at, new ActivityItem(m.Groups["t"].Value, "任务", Truncate(m.Groups["body"].Value, 90), "")));
            }
        }

        return items
            .OrderByDescending(x => x.At)
            .Take(take)
            .Select(x => x.Item)
            .ToList();
    }

    private static string Truncate(string s, int max)
    {
        var t = (s ?? "").Replace("\r", "").Replace('\n', ' ').Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }
}
