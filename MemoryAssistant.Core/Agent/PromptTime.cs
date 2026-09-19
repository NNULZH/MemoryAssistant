namespace MemoryAssistant.Core.Agent;

/// <summary>
/// 给提示词用的"当前时间"一行。
///
/// 为什么必须有：模型看不到系统时钟，提示词里不告诉它今天几号，它就只能靠工具返回的 Unix 时间戳
/// 自己推算日期（实测它确实在"约 2026-09"这种估算里打转）。于是"今天的记录""最新的消息"
/// 这类要求经常落空——用户明明看到时间线里已经有今天的消息，问出来却不是最新。
/// </summary>
public static class PromptTime
{
    public static string Line()
    {
        var now = DateTimeOffset.Now;
        var week = now.DayOfWeek switch
        {
            DayOfWeek.Monday => "一",
            DayOfWeek.Tuesday => "二",
            DayOfWeek.Wednesday => "三",
            DayOfWeek.Thursday => "四",
            DayOfWeek.Friday => "五",
            DayOfWeek.Saturday => "六",
            _ => "日",
        };
        return $"现在是 {now:yyyy-MM-dd HH:mm}（周{week}）。"
            + "判断「今天/昨天/最新/最近几天」一律以这个时间为准。"
            + "工具返回里每行都带 `time_text`（会话列表是 `last_time_text`），那已经是**本地时间**；"
            + "显示时间一律用它，**不要自己把 create_time 换算成日期**（模型没有时区概念，自己算会差 8 小时）。"
            + "要最新消息就必须真的读到数据库，不要凭印象说\"最近没什么\"。";
    }
}
