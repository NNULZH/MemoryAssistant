using System.Text.Json;
using MemoryAssistant.Core.Logging;
using MemoryAssistant.Core.PythonBridge;
using MemoryAssistant.Core.RAG;

namespace MemoryAssistant.Infrastructure.RAG;

/// <summary>
/// FTS 检索器：通过 Python Bridge 调用 search_messages（welive 全文搜索）。
/// 把消息结果聚合为"按会话+日期"的片段，与向量检索输出格式一致。
/// </summary>
public sealed class BridgeFtsRetriever : IRetriever
{
    public BridgeFtsRetriever(IPythonBridge bridge, IAppLogger? logger = null)
    {
        _bridge = bridge;
        _logger = logger;
    }

    private readonly IPythonBridge _bridge;
    private readonly IAppLogger? _logger;
    public string Name => "fts";

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        string query, int topK, CancellationToken ct = default)
    {
        var resp = await _bridge.RequestAsync(
            "search_messages",
            new Dictionary<string, object?> { ["keyword"] = query, ["limit"] = Math.Max(topK * 4, 10) },
            timeoutSeconds: 60,
            ct: ct);
        if (!resp.Success || resp.Data is not JsonElement je || je.ValueKind != JsonValueKind.Array)
            return [];

        // 按 (session_id, date) 聚合为片段
        var groups = new Dictionary<(string, string), (string Name, List<string> Lines)>();
        foreach (var m in je.EnumerateArray())
        {
            var sessionId = m.TryGetProperty("session_id", out var sid) ? sid.GetString() ?? "" : "";
            var sessionName = m.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? sessionId : sessionId;
            var createTime = m.TryGetProperty("create_time", out var ct2) ? ct2.GetInt64() : 0L;
            // 本地日期：用 UTC 日期会把"本地 00:30 的消息"算成前一天，时间窗过滤就会漏掉它
            var date = createTime == 0 ? "" : DateTimeOffset.FromUnixTimeSeconds(createTime).ToLocalTime().ToString("yyyy-MM-dd");
            if (date.Length == 0) continue;
            var sender = m.TryGetProperty("display_name", out var s) ? s.GetString() ?? "" : "";
            var content = m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            var key = (sessionId, date);
            if (!groups.TryGetValue(key, out var g))
                groups[key] = g = (sessionName, []);
            g.Lines.Add($"{sender}: {content}");
        }

        var outList = new List<RetrievedChunk>();
        foreach (var ((sessionId, date), (name, lines)) in groups)
        {
            outList.Add(new RetrievedChunk
            {
                SessionId = sessionId,
                SessionName = name,
                Date = date,
                Text = string.Join("\n", lines.Take(30)),
                MsgCount = lines.Count,
                Score = 1.0,
                Source = "fts",
            });
        }
        return outList.Take(topK).ToList();
    }
}
