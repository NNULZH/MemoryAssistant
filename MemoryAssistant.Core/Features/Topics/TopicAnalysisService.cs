using System.Text.Json;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Logging;
using MemoryAssistant.Core.PythonBridge;

namespace MemoryAssistant.Core.Features.Topics;

/// <summary>话题分析结果：命名主题列表 + 原始解析数据。</summary>
public sealed record TopicAnalysisResult(
    IReadOnlyList<NamedTopic> Topics,
    TopicAnalysis Raw);

/// <summary>
/// 话题分析服务：Bridge(rag_topics) → 解析 → LLM 命名 → 合并。
/// 关键词静态分类 + Embedding 聚类 + LLM 命名（混合方案）。
/// </summary>
public sealed class TopicAnalysisService
{
    private readonly IPythonBridge _bridge;
    private readonly IChatClient _chat;
    private readonly IAppLogger? _logger;

    public TopicAnalysisService(IPythonBridge bridge, IChatClient chat, IAppLogger? logger = null)
    {
        _bridge = bridge;
        _chat = chat;
        _logger = logger;
    }

    public async Task<TopicAnalysisResult> AnalyzeAsync(int recentDays = 14, CancellationToken ct = default)
    {
        var resp = await _bridge.RequestAsync("rag_topics", new Dictionary<string, object?>
        {
            ["recent_days"] = recentDays,
            ["max_clusters"] = 10,
        }, timeoutSeconds: 60, ct: ct);

        if (!resp.Success || resp.Data is not JsonElement je)
            return new TopicAnalysisResult([], new TopicAnalysis([], [], 0));

        var raw = TopicDataParser.Parse(je);

        var namer = new TopicNamer(_chat, _logger);
        var named = await namer.NameClustersAsync(raw.Clusters, maxClusters: 8, ct);

        var merged = TopicMerger.Merge(raw.KeywordTopics, named, raw.Clusters);
        return new TopicAnalysisResult(merged, raw);
    }
}
