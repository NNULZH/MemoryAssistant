using System.Text.Json;
using MemoryAssistant.Core.Features.Topics;

namespace MemoryAssistant.Core.Features.Profiles;

/// <summary>会话画像（数据统计，不做心理诊断）。</summary>
public sealed record SessionProfile(
    string SessionId,
    string SessionName,
    int MsgCount,
    int DayCount,
    string FirstDate,
    string LastDate,
    IReadOnlyList<KeywordTopic> TopKeywordTopics);

/// <summary>解析 rag_profiles 响应的 "profiles" 数组（纯函数，可单测）。</summary>
public static class ProfileDataParser
{
    public static IReadOnlyList<SessionProfile> Parse(JsonElement profiles)
    {
        if (profiles.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<SessionProfile>();
        foreach (var p in profiles.EnumerateArray())
        {
            var sessionId = GetString(p, "session_id") ?? "";
            var topics = new List<KeywordTopic>();
            if (p.TryGetProperty("top_keyword_topics", out var kw) && kw.ValueKind == JsonValueKind.Array)
            {
                foreach (var k in kw.EnumerateArray())
                {
                    topics.Add(new KeywordTopic(
                        GetString(k, "category") ?? "",
                        GetInt(k, "count"),
                        []));
                }
            }
            list.Add(new SessionProfile(
                sessionId,
                GetString(p, "session_name") ?? sessionId,
                GetInt(p, "msg_count"),
                GetInt(p, "day_count"),
                GetString(p, "first_date") ?? "",
                GetString(p, "last_date") ?? "",
                topics));
        }
        return list;
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}

/// <summary>小时活跃分布（24 桶，本地时区）。</summary>
public static class HourlyHistogram
{
    public static int[] Compute(IReadOnlyList<long> unixSeconds)
    {
        var hist = new int[24];
        foreach (var ts in unixSeconds)
        {
            if (ts <= 0) continue;
            var hour = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.Hour;
            if (hour is >= 0 and < 24) hist[hour]++;
        }
        return hist;
    }

    /// <summary>返回 TopN 活跃时段 [(小时, 条数)]，按条数降序。</summary>
    public static IReadOnlyList<(int Hour, int Count)> TopHours(int[] hist, int topN = 3)
    {
        var idx = Enumerable.Range(0, 24)
            .Select(i => (Hour: i, Count: hist[i]))
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .Take(topN)
            .ToList();
        return idx;
    }
}
