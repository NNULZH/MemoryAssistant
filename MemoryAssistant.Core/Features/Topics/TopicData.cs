using System.Text.Json;

namespace MemoryAssistant.Core.Features.Topics;

/// <summary>某日计数。</summary>
public sealed record DayCount(string Date, int Count);

/// <summary>关键词分类主题。</summary>
public sealed record KeywordTopic(string Category, int Total, IReadOnlyList<DayCount> PerDay);

/// <summary>主题下的一个消息片段（带起始时间，供"跳到那段对话"定位用）。</summary>
public sealed record TopicSample(
    string SessionId,
    string SessionName,
    string Date,
    string Text,
    long CreateTime = 0);

/// <summary>Embedding 聚类得到的簇（未命名）。</summary>
public sealed record TopicCluster(
    int ClusterId,
    int Size,
    string Representative,
    IReadOnlyList<DayCount> PerDay,
    IReadOnlyList<TopicSample> Samples);

/// <summary>rag_topics 整体结果。</summary>
public sealed record TopicAnalysis(
    IReadOnlyList<KeywordTopic> KeywordTopics,
    IReadOnlyList<TopicCluster> Clusters,
    int ChunksInScope);

/// <summary>解析 rag_topics 响应（纯函数，可单测）。</summary>
public static class TopicDataParser
{
    public static TopicAnalysis Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
            return new TopicAnalysis([], [], 0);

        var chunks = data.TryGetProperty("chunks_in_scope", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetInt32() : 0;
        var kw = data.TryGetProperty("keyword_topics", out var k) ? ParseKeywordTopics(k) : [];
        var cl = data.TryGetProperty("clusters", out var clElem) ? ParseClusters(clElem) : [];
        return new TopicAnalysis(kw, cl, chunks);
    }

    public static IReadOnlyList<KeywordTopic> ParseKeywordTopics(JsonElement arr)
    {
        if (arr.ValueKind != JsonValueKind.Array) return [];
        var list = new List<KeywordTopic>();
        foreach (var e in arr.EnumerateArray())
        {
            list.Add(new KeywordTopic(
                GetString(e, "category") ?? "",
                GetInt(e, "total"),
                ParseDayCounts(e, "per_day")));
        }
        return list;
    }

    public static IReadOnlyList<TopicCluster> ParseClusters(JsonElement arr)
    {
        if (arr.ValueKind != JsonValueKind.Array) return [];
        var list = new List<TopicCluster>();
        foreach (var e in arr.EnumerateArray())
        {
            var samples = new List<TopicSample>();
            if (e.TryGetProperty("samples", out var ss) && ss.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in ss.EnumerateArray())
                {
                    samples.Add(new TopicSample(
                        GetString(s, "session_id") ?? "",
                        GetString(s, "session_name") ?? "",
                        GetString(s, "date") ?? "",
                        GetString(s, "text") ?? "",
                        GetLong(s, "create_time")));
                }
            }
            list.Add(new TopicCluster(
                GetInt(e, "cluster_id"),
                GetInt(e, "size"),
                GetString(e, "representative") ?? "",
                ParseDayCounts(e, "per_day"),
                samples));
        }
        return list;
    }

    private static IReadOnlyList<DayCount> ParseDayCounts(JsonElement e, string name)
    {
        var list = new List<DayCount>();
        if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var d in arr.EnumerateArray())
        {
            list.Add(new DayCount(GetString(d, "date") ?? "", GetInt(d, "count")));
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
