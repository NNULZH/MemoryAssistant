using System.Text.Json;
using MemoryAssistant.Core.Logging;
using MemoryAssistant.Core.PythonBridge;
using MemoryAssistant.Core.RAG;

namespace MemoryAssistant.Infrastructure.RAG;

/// <summary>
/// 向量检索器：通过 Python Bridge 调用 rag_search。
/// </summary>
public sealed class BridgeRagRetriever : IRetriever
{
    public BridgeRagRetriever(IPythonBridge bridge, IAppLogger? logger = null)
    {
        _bridge = bridge;
        _logger = logger;
    }

    private readonly IPythonBridge _bridge;
    private readonly IAppLogger? _logger;
    public string Name => "vector";

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        string query, int topK, CancellationToken ct = default)
    {
        var resp = await _bridge.RequestAsync(
            "rag_search",
            new Dictionary<string, object?> { ["query"] = query, ["top_k"] = topK },
            timeoutSeconds: 60,
            ct: ct);
        if (!resp.Success)
        {
            _logger?.Warn($"rag_search 失败: {resp.Error}");
            return [];
        }
        if (resp.Data is not JsonElement je || je.ValueKind != JsonValueKind.Array)
            return [];

        var outList = new List<RetrievedChunk>();
        foreach (var item in je.EnumerateArray())
        {
            if (!item.TryGetProperty("chunk", out var chunk)) continue;
            var c = ParseChunk(chunk);
            if (c is null) continue;
            c = c with
            {
                Score = item.TryGetProperty("score", out var s) ? s.GetDouble() : 0,
                Source = "vector",
            };
            outList.Add(c);
        }
        return outList;
    }

    public static RetrievedChunk? ParseChunk(JsonElement chunk)
    {
        var sessionId = chunk.TryGetProperty("session_id", out var sid) ? sid.GetString() ?? "" : "";
        var sessionName = chunk.TryGetProperty("session_name", out var sn) ? sn.GetString() ?? sessionId : sessionId;
        var date = chunk.TryGetProperty("date", out var d) ? d.GetString() ?? "" : "";
        var text = chunk.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        var msgCount = chunk.TryGetProperty("msg_count", out var mc) ? mc.GetInt32() : 0;
        return new RetrievedChunk
        {
            SessionId = sessionId,
            SessionName = sessionName,
            Date = date,
            Text = text,
            MsgCount = msgCount,
        };
    }
}
