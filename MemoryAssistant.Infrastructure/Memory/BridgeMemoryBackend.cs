using System.Text;
using System.Text.Json;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Skills;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.Logging;
using MemoryAssistant.Core.PythonBridge;
using MemoryAssistant.Core.RAG;
using MemoryAssistant.Infrastructure.RAG;

namespace MemoryAssistant.Infrastructure.Memory;

/// <summary>
/// IMemoryBackend 的真实实现：把 IPythonBridge（wxchat/rag_*）与 IRetriever（RAG）
/// 封装成 Skill 需要的原子能力。JSON 解析全部防御式，出错返回空集合并记日志。
/// 读取参数受 AgentOptions.Eco 上限钳制（验收默认小样本）。
/// </summary>
public sealed class BridgeMemoryBackend : IMemoryBackend
{
    private readonly IPythonBridge _bridge;
    private readonly IRetriever _retriever;
    private readonly IAppLogger? _logger;
    private readonly AgentOptions _options;
    private static readonly TimeSpan LocalOffset = TimeSpan.FromHours(8); // wxchat create_time 为本地秒

    public BridgeMemoryBackend(IPythonBridge bridge, IRetriever retriever, AgentOptions options, IAppLogger? logger = null)
    {
        _bridge = bridge;
        _retriever = retriever;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SemanticSearchAsync(string query, int topK, CancellationToken ct)
    {
        var k = Clamp(topK, _options.EcoMode ? _options.EcoMaxTopK : topK);
        return await _retriever.SearchAsync(query, k, ct);
    }

    public async Task<IReadOnlyList<Evidence>> ReadDayMessagesAsync(string sessionId, string date, int limit, CancellationToken ct)
    {
        var (begin, end) = DayRange(date);
        var cap = Clamp(limit, _options.EcoMode ? _options.EcoMaxReadLimit : limit);
        var resp = await _bridge.RequestAsync("read_messages", new Dictionary<string, object?>
        {
            ["session_id"] = sessionId, ["limit"] = cap, ["begin"] = begin, ["end"] = end,
        }, timeoutSeconds: 30, ct: ct);
        // read_messages 不回传 session_id（它是入参）：这里补回去，否则证据卡片点不进聊天记录
        return ParseMessages(resp, "微信原文", fallbackSessionId: sessionId, fallbackDate: date);
    }

    public async Task<IReadOnlyList<Evidence>> KeywordSearchAsync(string keyword, string? sessionId, int limit, CancellationToken ct)
    {
        var cap = Clamp(limit, _options.EcoMode ? _options.EcoMaxReadLimit : limit);
        var args = new Dictionary<string, object?> { ["keyword"] = keyword, ["limit"] = cap };
        if (!string.IsNullOrWhiteSpace(sessionId)) args["session_id"] = sessionId;
        var resp = await _bridge.RequestAsync("search_messages", args, timeoutSeconds: 30, ct: ct);
        return ParseMessages(resp, "微信原文", fallbackSessionId: sessionId);
    }

    public async Task<string> GetStatsTextAsync(int limit, int sessionLimit, CancellationToken ct)
    {
        var l = Clamp(limit, _options.EcoMode ? _options.EcoMaxReadLimit : limit);
        var sl = Clamp(sessionLimit, _options.EcoMode ? _options.EcoMaxSessionLimit : sessionLimit);
        var resp = await _bridge.RequestAsync("get_session_stats", new Dictionary<string, object?>
        {
            ["limit"] = l, ["session_limit"] = sl,
        }, timeoutSeconds: 60, ct: ct);
        if (!resp.Success || resp.Data is not JsonElement je || je.ValueKind != JsonValueKind.Object)
        {
            _logger?.Warn($"get_session_stats 失败: {resp.Error}");
            return "";
        }
        var sb = new StringBuilder();
        sb.Append($"活跃会话 {Get(je, "total_sessions")} 个，消息 {Get(je, "total_messages")} 条，活跃 {Get(je, "active_days")} 天。");

        // 会话排行："最近和谁聊得最多"问的是会话（人/群），不是发送者，也不该被公众号推送占榜。
        if (je.TryGetProperty("top_sessions", out var topSess) && topSess.ValueKind == JsonValueKind.Array)
        {
            var all = topSess.EnumerateArray()
                .Select(s =>
                {
                    var username = Str(s, "username") ?? "";
                    var name = Str(s, "display_name");
                    return (
                        User: username,
                        Name: string.IsNullOrWhiteSpace(name) ? username : name!,
                        Count: Get(s, "count"),
                        Broadcast: Bool(s, "is_broadcast") || IsBroadcast(username));
                })
                .ToList();

            var conversations = all.Where(x => !x.Broadcast && x.Name.Length > 0).Take(8).ToList();
            var skipped = all.Count(x => x.Broadcast);
            sb.Append(conversations.Count > 0
                ? "高频会话：" + string.Join("、", conversations.Select(x => $"{x.Name}({x.Count}条样本)")) + "。"
                : "高频会话：这段时间没有与人/群的往来记录。");
            if (skipped > 0) sb.Append($"（另有 {skipped} 个公众号/服务号推送未计入）。");
        }

        if (je.TryGetProperty("top_senders", out var senders) && senders.ValueKind == JsonValueKind.Array)
        {
            var lines = senders.EnumerateArray()
                .Select(s =>
                {
                    var username = Str(s, "username") ?? "";
                    var name = Str(s, "display_name");
                    return (Name: string.IsNullOrWhiteSpace(name) ? username : name!, Count: Get(s, "count"));
                })
                .Where(x => !IsBroadcast(x.Name) && x.Name.Length > 0)
                .Take(5)
                .Select(x => $"{x.Name}({x.Count})")
                .ToList();
            if (lines.Count > 0) sb.Append("高频发言：").AppendJoin("、", lines).Append('。');
        }

        // 样本口径必须让用户知道：统计是"最近抽样"，不是全量精确值
        var note = Str(je, "note");
        if (!string.IsNullOrWhiteSpace(note)) sb.Append(note!.Trim().TrimEnd('。')).Append('。');

        return sb.Length > 1200 ? sb.ToString()[..1200] : sb.ToString();
    }

    /// <summary>公众号/服务号/品牌占位会话：属于"推送"而非"聊天对象"。</summary>
    private static bool IsBroadcast(string usernameOrName)
        => usernameOrName.StartsWith("gh_", StringComparison.OrdinalIgnoreCase)
           || usernameOrName.Contains("brandsessionholder", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<SessionHit>> FindSessionsAsync(
        string keyword, bool? privateOnly, int limit, CancellationToken ct)
    {
        var resp = await _bridge.RequestAsync("find_sessions", new Dictionary<string, object?>
        {
            ["keyword"] = keyword,
            ["private_only"] = privateOnly,
            ["limit"] = limit,
        }, timeoutSeconds: 60, ct: ct);

        var outList = new List<SessionHit>();
        if (!resp.Success || resp.Data is not JsonElement je || je.ValueKind != JsonValueKind.Array)
        {
            _logger?.Warn($"find_sessions 失败: {resp.Error}");
            return outList;
        }
        foreach (var s in je.EnumerateArray())
        {
            var id = Str(s, "username") ?? "";
            if (id.Length == 0) continue;
            outList.Add(new SessionHit(
                id,
                Str(s, "display_name") ?? "",
                Bool(s, "is_chatroom"),
                Long(s, "last_timestamp")));
        }
        return outList;
    }

    public async Task<IReadOnlyList<RenderedMessage>> ReadRenderedAsync(
        string sessionId, long begin, long end, int limit, CancellationToken ct)
    {
        var resp = await _bridge.RequestAsync("read_rendered", new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
            ["begin"] = begin,
            ["end"] = end,
            ["limit"] = limit,
            ["media_dir"] = MediaDirectory,
        }, timeoutSeconds: 300, ct: ct);

        var outList = new List<RenderedMessage>();
        if (!resp.Success || resp.Data is not JsonElement je || je.ValueKind != JsonValueKind.Array)
        {
            _logger?.Warn($"read_rendered 失败: {resp.Error}");
            return outList;
        }
        foreach (var m in je.EnumerateArray())
        {
            outList.Add(new RenderedMessage
            {
                LocalId = Long(m, "local_id"),
                CreateTime = Long(m, "create_time"),
                SenderName = Str(m, "display_name") ?? Str(m, "sender_username") ?? "",
                IsSelf = Bool(m, "is_self"),
                Kind = Str(m, "kind") ?? "text",
                Text = Str(m, "text") ?? "",
                MediaPath = Str(m, "media_path") ?? "",
                MediaError = Str(m, "media_error") ?? "",
            });
        }
        return outList;
    }

    /// <summary>图片/语音解密落盘目录（与 appsettings.json 同样的"exe 同目录 data"约定）。</summary>
    public static string MediaDirectory
    {
        get
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "data", "media");
            try { Directory.CreateDirectory(dir); } catch { /* 目录建不出来时由下游如实报错 */ }
            return dir;
        }
    }

    public async Task<IReadOnlyList<DayActivity>> GetRecentDaysAsync(int count, CancellationToken ct)
    {
        var resp = await _bridge.RequestAsync("rag_timeline", null, timeoutSeconds: 60, ct: ct);
        var outList = new List<DayActivity>();
        if (!resp.Success || resp.Data is not JsonElement je ||
            !je.TryGetProperty("days", out var days) || days.ValueKind != JsonValueKind.Array)
        {
            _logger?.Warn($"rag_timeline 失败: {resp.Error}");
            return outList;
        }
        foreach (var d in days.EnumerateArray().Take(count))
        {
            var date = Str(d, "date") ?? "";
            var msg = d.TryGetProperty("msg_count", out var m) ? m.GetInt32() : 0;
            if (!string.IsNullOrEmpty(date)) outList.Add(new DayActivity(date, msg));
        }
        return outList;
    }

    public async Task<IReadOnlyList<Evidence>> GetDaySnippetsAsync(string date, CancellationToken ct)
    {
        var resp = await _bridge.RequestAsync("rag_day_detail", new Dictionary<string, object?> { ["date"] = date }, timeoutSeconds: 30, ct: ct);
        var outList = new List<Evidence>();
        if (!resp.Success || resp.Data is not JsonElement je ||
            !je.TryGetProperty("chunks", out var chunks) || chunks.ValueKind != JsonValueKind.Array)
        {
            _logger?.Warn($"rag_day_detail({date}) 失败: {resp.Error}");
            return outList;
        }
        // 按天取片段：一天可能有几十个片段，之前只取前 20 个（还是按会话名排序的），
        // 用户问的那个会话经常被截掉 → 直接判"没有"。一天的片段量很小，全取回来。
        foreach (var c in chunks.EnumerateArray().Take(200))
        {
            var sid = Str(c, "session_id") ?? "";
            var sname = Str(c, "session_name") ?? sid;
            var text = Str(c, "text") ?? "";
            if (string.IsNullOrEmpty(sid) && string.IsNullOrEmpty(text)) continue;
            outList.Add(new Evidence
            {
                SessionId = sid,
                SessionDisplayName = sname,
                Date = date,
                Content = text,
                Source = "时间线",
            });
        }
        return outList;
    }

    public async Task<IReadOnlyList<Evidence>> ScanCommitmentsAsync(int limit, CancellationToken ct)
    {
        var cap = Clamp(limit, _options.EcoMode ? _options.EcoMaxReadLimit : limit);
        var resp = await _bridge.RequestAsync("rag_commitments",
            new Dictionary<string, object?> { ["limit"] = cap }, timeoutSeconds: 60, ct: ct);
        var outList = new List<Evidence>();
        if (!resp.Success || resp.Data is not JsonElement je ||
            !je.TryGetProperty("candidates", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            _logger?.Warn($"rag_commitments 失败: {resp.Error}");
            return outList;
        }
        foreach (var c in arr.EnumerateArray())
        {
            var sid = Str(c, "session_id") ?? "";
            var sname = Str(c, "session_name") ?? sid;
            var content = Str(c, "content") ?? "";
            if (content.Length == 0) continue;
            outList.Add(new Evidence
            {
                SessionId = sid,
                SessionDisplayName = sname,
                CreateTime = Long(c, "create_time"),
                SenderName = Str(c, "sender") ?? "",
                Content = content,
                Source = "承诺",
            });
        }
        return outList;
    }

    public async Task<string> GetTopicsTextAsync(int recentDays, CancellationToken ct)
    {
        var resp = await _bridge.RequestAsync("rag_topics",
            new Dictionary<string, object?> { ["recent_days"] = recentDays }, timeoutSeconds: 120, ct: ct);
        if (!resp.Success || resp.Data is not JsonElement je)
        {
            _logger?.Warn($"rag_topics 失败: {resp.Error}");
            return "";
        }
        var sb = new StringBuilder($"近 {recentDays} 天话题：");
        if (je.TryGetProperty("keyword_topics", out var kw) && kw.ValueKind == JsonValueKind.Array)
        {
            var names = kw.EnumerateArray().Take(10)
                .Select(x => $"{Str(x, "category")}({Get(x, "total")})").Where(x => !x.StartsWith('(')).ToList();
            if (names.Count > 0) sb.Append("关键词：").AppendJoin("、", names).Append('。');
        }
        if (je.TryGetProperty("clusters", out var cl) && cl.ValueKind == JsonValueKind.Array)
        {
            var reps = cl.EnumerateArray().Take(6)
                .Select(x => Str(x, "representative")).Where(r => !string.IsNullOrEmpty(r)).ToList();
            if (reps.Count > 0) sb.Append("聚类代表：").AppendJoin("；", reps).Append('。');
        }
        return sb.Length > 1000 ? sb.ToString()[..1000] : sb.ToString();
    }

    public async Task<string> GetProfilesTextAsync(int top, CancellationToken ct)
    {
        var resp = await _bridge.RequestAsync("rag_profiles",
            new Dictionary<string, object?> { ["top"] = top }, timeoutSeconds: 120, ct: ct);
        if (!resp.Success || resp.Data is not JsonElement je ||
            !je.TryGetProperty("profiles", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            _logger?.Warn($"rag_profiles 失败: {resp.Error}");
            return "";
        }
        var sb = new StringBuilder($"画像概览（前 {Math.Min(top, arr.GetArrayLength())}）：");
        var lines = new List<string>();
        foreach (var p in arr.EnumerateArray().Take(top))
        {
            var name = Str(p, "session_name") ?? Str(p, "session_id") ?? "?";
            var msg = Get(p, "msg_count");
            var days = Get(p, "day_count");
            var topic = Str(p, "top_keyword_topic");
            var extra = !string.IsNullOrEmpty(topic) ? $"/{topic}" : "";
            lines.Add($"{name}({msg}条/{days}天{extra})");
        }
        sb.AppendJoin("；", lines).Append('。');
        return sb.Length > 1000 ? sb.ToString()[..1000] : sb.ToString();
    }

    // ---------- helpers ----------

    private static int Clamp(int value, int cap)
    {
        if (cap <= 0) return Math.Max(1, value);
        return Math.Max(1, Math.Min(value, cap));
    }

    private static (long Begin, long End) DayRange(string date)
    {
        if (!DateTimeOffset.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var day))
            return (0, 0);
        var begin = new DateTimeOffset(day.Year, day.Month, day.Day, 0, 0, 0, LocalOffset).ToUnixTimeSeconds();
        return (begin, begin + 86400);
    }

    private IReadOnlyList<Evidence> ParseMessages(
        BridgeResponse resp, string source, string? fallbackSessionId = null, string fallbackDate = "")
    {
        var outList = new List<Evidence>();
        if (!resp.Success)
        {
            _logger?.Warn($"读取消息失败: {resp.Error}");
            return outList;
        }
        if (resp.Data is not JsonElement je || je.ValueKind != JsonValueKind.Array) return outList;
        foreach (var m in je.EnumerateArray())
        {
            var content = Str(m, "content") ?? "";
            if (content.Length == 0) continue;
            var createTime = Long(m, "create_time");
            outList.Add(new Evidence
            {
                SessionId = Str(m, "session_id") ?? Str(m, "username") ?? fallbackSessionId ?? "",
                SessionDisplayName = Str(m, "display_name") ?? "",
                CreateTime = createTime,
                // 日期只有材料说了算：优先用查询时确定的日期，否则从消息时间推
                Date = !string.IsNullOrEmpty(fallbackDate)
                    ? fallbackDate
                    : createTime > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(createTime).ToLocalTime().ToString("yyyy-MM-dd")
                        : "",
                SenderName = Str(m, "display_name") ?? Str(m, "sender") ?? "",
                Content = content,
                Source = source,
            });
        }
        return outList;
    }

    private static string? Str(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static long Get(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static bool Bool(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => v.GetInt64() != 0,
            JsonValueKind.String => v.GetString() is "1" or "true",
            _ => false,
        };
}
