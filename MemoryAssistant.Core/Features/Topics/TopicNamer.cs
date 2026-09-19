using System.Text.Json;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Logging;

namespace MemoryAssistant.Core.Features.Topics;

/// <summary>LLM 给聚类簇命名。</summary>
public sealed class TopicNamer
{
    private readonly IChatClient _chat;
    private readonly IAppLogger? _logger;

    public TopicNamer(IChatClient chat, IAppLogger? logger = null)
    {
        _chat = chat;
        _logger = logger;
    }

    public sealed record NamedCluster(int ClusterId, string Label);

    private const string SystemPrompt = """
你是聊天主题命名器。用户会给你若干聊天消息聚类簇（cluster_id + 代表性文本）。
请为每个簇起一个 4~10 字的中文主题名（如"找实习""考研复习""开黑游戏"）。
只输出 JSON，不要多余文字：
{"topics":[{"cluster_id":0,"label":"主题名"}]}
""";

    /// <summary>给 top 簇（按 size 降序，最多 maxClusters 个）命名；LLM 失败时回退"主题N"。</summary>
    public async Task<IReadOnlyList<NamedCluster>> NameClustersAsync(
        IReadOnlyList<TopicCluster> clusters,
        int maxClusters = 8,
        CancellationToken ct = default)
    {
        var top = clusters.OrderByDescending(c => c.Size).Take(maxClusters).ToList();
        if (top.Count == 0) return [];

        var user = string.Join("\n\n", top.Select((c, i) =>
            $"cluster_id={c.ClusterId} (样本量 {c.Size})\n代表文本：{Truncate(c.Representative, 150)}"));

        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = SystemPrompt },
            new() { Role = "user", Content = user },
        };

        try
        {
            var result = await _chat.ChatAsync(messages, ct: ct);
            var labels = ParseLabels(result.Content ?? "");
            if (labels is { Count: > 0 })
                return labels;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"TopicNamer 失败: {ex.Message}");
        }

        // 回退：主题N
        return top.Select((c, i) => new NamedCluster(c.ClusterId, $"主题{i + 1}")).ToList();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];

    /// <summary>解析 LLM 输出的 JSON（容忍 markdown 代码块，取首尾花括号）。</summary>
    public static IReadOnlyList<NamedCluster> ParseLabels(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return [];
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start) return [];
        try
        {
            using var doc = JsonDocument.Parse(content[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("topics", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];
            var list = new List<NamedCluster>();
            foreach (var t in arr.EnumerateArray())
            {
                if (!t.TryGetProperty("cluster_id", out var id) || id.ValueKind != JsonValueKind.Number) continue;
                var label = t.TryGetProperty("label", out var lb) ? lb.GetString() : null;
                if (string.IsNullOrWhiteSpace(label)) continue;
                list.Add(new NamedCluster(id.GetInt32(), label.Trim()));
            }
            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
