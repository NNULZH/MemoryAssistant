using System.Text.Json;

namespace MemoryAssistant.Core.Timeline;

/// <summary>时间线中的一个会话聚合。</summary>
public sealed record TimelineSession(string SessionId, string SessionName, int MsgCount);

/// <summary>时间线中的一天。</summary>
public sealed record TimelineDayInfo(
    string Date,
    int MsgCount,
    int SessionCount,
    IReadOnlyList<TimelineSession> Sessions);

/// <summary>某日某会话的片段详情（带日期与片段起始时间，供"跳到那段对话"定位用）。</summary>
public sealed record DayChunkInfo(
    string SessionId,
    string SessionName,
    string Text,
    int MsgCount,
    string Date,
    long CreateTime);

/// <summary>rag_timeline 解析结果：天列表 + 月份列表（降序）。</summary>
public sealed record TimelineData(IReadOnlyList<TimelineDayInfo> Days, IReadOnlyList<string> Months);

/// <summary>把 Bridge 返回的 rag_timeline / rag_day_detail JSON 解析为强类型数据。
/// 纯函数，便于单元测试；UI 层只做展示映射。</summary>
public static class TimelineDataParser
{
    /// <summary>解析 rag_timeline 的 "days" 数组；非法输入返回空数据（不抛异常）。</summary>
    public static TimelineData ParseDays(JsonElement days)
    {
        if (days.ValueKind != JsonValueKind.Array)
            return new TimelineData([], []);

        var list = new List<TimelineDayInfo>();
        var months = new HashSet<string>();
        foreach (var d in days.EnumerateArray())
        {
            var date = GetString(d, "date") ?? "";
            if (date.Length < 7) continue;
            months.Add(date[..7]);

            var sessions = new List<TimelineSession>();
            if (d.TryGetProperty("sessions", out var ss) && ss.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in ss.EnumerateArray())
                {
                    sessions.Add(new TimelineSession(
                        GetString(s, "session_id") ?? "",
                        GetString(s, "session_name") ?? "",
                        GetInt(s, "msg_count")));
                }
            }
            list.Add(new TimelineDayInfo(
                date,
                GetInt(d, "msg_count"),
                GetInt(d, "session_count"),
                sessions));
        }
        list.Sort((a, b) => string.CompareOrdinal(b.Date, a.Date));
        return new TimelineData(list, months.OrderByDescending(x => x).ToList());
    }

    /// <summary>解析 rag_day_detail 的 "chunks" 数组；非法输入返回空列表。</summary>
    public static IReadOnlyList<DayChunkInfo> ParseDayChunks(JsonElement chunks)
    {
        if (chunks.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<DayChunkInfo>();
        foreach (var c in chunks.EnumerateArray())
        {
            list.Add(new DayChunkInfo(
                GetString(c, "session_id") ?? "",
                GetString(c, "session_name") ?? "",
                GetString(c, "text") ?? "",
                GetInt(c, "msg_count"),
                GetString(c, "date") ?? "",
                GetLong(c, "create_time")));
        }
        return list;
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static long GetLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0L;
}
