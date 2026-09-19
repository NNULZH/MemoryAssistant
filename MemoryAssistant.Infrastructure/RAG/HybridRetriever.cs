using MemoryAssistant.Core.Logging;
using MemoryAssistant.Core.RAG;

namespace MemoryAssistant.Infrastructure.RAG;

/// <summary>
/// 混合检索器：向量（语义） + FTS（关键词），按排名加权融合。
/// 向量结果权重更高（语义主检索），FTS 提供精确关键词命中。
/// </summary>
public sealed class HybridRetriever : IRetriever
{
    public HybridRetriever(
        IRetriever vectorRetriever,
        IRetriever ftsRetriever,
        double vectorWeight = 0.7,
        double ftsWeight = 0.3,
        int topK = 5)
    {
        _vector = vectorRetriever;
        _fts = ftsRetriever;
        _vectorWeight = vectorWeight;
        _ftsWeight = ftsWeight;
        _topK = topK;
    }

    private readonly IRetriever _vector;
    private readonly IRetriever _fts;
    private readonly double _vectorWeight;
    private readonly double _ftsWeight;
    private readonly int _topK;
    public string Name => "hybrid";

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        string query, int topK, CancellationToken ct = default)
    {
        var vectorTask = _vector.SearchAsync(query, Math.Max(topK * 2, _topK), ct);
        var ftsTask = _fts.SearchAsync(query, Math.Max(topK, 5), ct);
        await Task.WhenAll(vectorTask, ftsTask);

        // rank-based 分数：第 i 名 → score = 1/(i+1) * weight
        var merged = new Dictionary<string, (RetrievedChunk Chunk, double Score)>();
        int i = 0;
        foreach (var c in vectorTask.Result)
        {
            var key = $"{c.SessionId}|{c.Date}";
            var rankScore = (1.0 / (i + 1)) * _vectorWeight;
            if (merged.TryGetValue(key, out var cur))
                merged[key] = (c, cur.Score + rankScore);
            else
                merged[key] = (c with { Source = "hybrid" }, rankScore);
            i++;
        }
        i = 0;
        foreach (var c in ftsTask.Result)
        {
            var key = $"{c.SessionId}|{c.Date}";
            var rankScore = (1.0 / (i + 1)) * _ftsWeight;
            if (merged.TryGetValue(key, out var cur))
                merged[key] = (cur.Chunk with { Score = cur.Score + rankScore, Source = "hybrid" }, cur.Score + rankScore);
            else
                merged[key] = (c with { Score = rankScore, Source = "hybrid" }, rankScore);
            i++;
        }

        return merged.Values
            .OrderByDescending(x => x.Score)
            .Select(x => x.Chunk with { Score = Math.Round(x.Score, 4) })
            .Take(topK)
            .ToList();
    }
}
