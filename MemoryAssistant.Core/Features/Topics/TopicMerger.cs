namespace MemoryAssistant.Core.Features.Topics;

/// <summary>合并后的命名主题（关键词主题 + 聚类主题）。</summary>
public sealed record NamedTopic(
    string Label,
    string Kind,             // "关键词主题" | "聚类主题"
    int TotalSize,
    IReadOnlyList<DayCount> PerDay,
    IReadOnlyList<TopicSample> Samples,
    string Representative);

/// <summary>合并关键词主题与 LLM 命名的聚类簇（纯函数，可单测）。</summary>
public static class TopicMerger
{
    public static IReadOnlyList<NamedTopic> Merge(
        IReadOnlyList<KeywordTopic> keywords,
        IReadOnlyList<TopicNamer.NamedCluster> namedClusters,
        IReadOnlyList<TopicCluster> clusters)
    {
        var result = new List<NamedTopic>();

        foreach (var k in keywords)
        {
            result.Add(new NamedTopic(
                k.Category,
                "关键词主题",
                k.Total,
                k.PerDay,
                [],
                ""));
        }

        foreach (var c in clusters)
        {
            var label = namedClusters.FirstOrDefault(n => n.ClusterId == c.ClusterId)?.Label
                        ?? $"主题{c.ClusterId + 1}";
            result.Add(new NamedTopic(
                label,
                "聚类主题",
                c.Size,
                c.PerDay,
                c.Samples,
                c.Representative));
        }

        return result
            .OrderByDescending(t => t.TotalSize)
            .ToList();
    }
}
