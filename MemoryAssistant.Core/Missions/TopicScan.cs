using System.Text.Json;

namespace MemoryAssistant.Core.Missions;

/// <summary>一次主题扫描命中的一条消息（跨会话检索到的原文）。</summary>
public sealed record TopicHit(string SessionId, long At, string Who, string Content, string Keyword);

/// <summary>优先队列里的一项：这个会话本周期命中多少、历史累计多少、要不要深扫。</summary>
public sealed record TopicRank(string SessionId, int Hits, int Score, bool DeepScan);

/// <summary>
/// 主题监听的关键词（V4.1）：**模型自己长脑子**——把用户给的主题扩成聊天里真能搜到的相关词
/// （"就业信息" → 校招/内推/招聘/秋招），规则只在模型不可用时兜底成主题本身。
/// 抽词与解析放 Core 是为了能脱网单测（不依赖 Bridge 与网络）。
/// </summary>
public static class TopicKeywords
{
    /// <summary>一轮最多用几个词检索（词越多，Bridge 往返与耗时越多）。</summary>
    public const int MaxKeywords = 6;

    /// <summary>扩词提示词：要求"能被直接搜到的说法"，而不是抽象概括。</summary>
    public const string SystemPrompt = """
        你是"主题监听"的关键词扩展器：用户给一个主题，你给出能在聊天记录里**原样搜到**的相关说法。
        要求：
        1. 3~6 个词，中文为主，每个 2~6 个字，都是聊天里会真实出现的词（如 校招、内推、招聘、秋招、实习、offer）。
        2. 不要抽象概括词（如"职业发展"这种没人会照原样打出来的），不要解释、不要标点结尾。
        3. 只输出 JSON，不要任何多余文字：{"keywords":["校招","内推"]}
        """;

    /// <summary>
    /// 解析模型输出（容忍 ```json 围栏与前后说明文字）；解析不出就退回主题本身——
    /// 宁可用字面词继续搜，也不因为模型抽风就不扫。
    /// </summary>
    public static List<string> Parse(string? llmOutput, string topic)
    {
        var text = llmOutput ?? "";

        // 优先认 {"keywords":[...]}
        var objStart = text.IndexOf('{');
        var objEnd = text.LastIndexOf('}');
        if (objStart >= 0 && objEnd > objStart)
        {
            try
            {
                using var doc = JsonDocument.Parse(text[objStart..(objEnd + 1)]);
                if (doc.RootElement.TryGetProperty("keywords", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    var list = Clean(arr.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : ""));
                    if (list.Count > 0) return list;
                }
            }
            catch (JsonException) { /* 落到裸数组解析 */ }
        }

        // 退一步认裸数组 ["校招","内推"]
        var arrStart = text.IndexOf('[');
        var arrEnd = text.LastIndexOf(']');
        if (arrStart >= 0 && arrEnd > arrStart)
        {
            try
            {
                using var doc = JsonDocument.Parse(text[arrStart..(arrEnd + 1)]);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var list = Clean(doc.RootElement.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : ""));
                    if (list.Count > 0) return list;
                }
            }
            catch (JsonException) { /* 退回主题本身 */ }
        }

        var fallback = new List<string>();
        var t = (topic ?? "").Trim();
        if (t.Length > 0) fallback.Add(t);
        return fallback;
    }

    /// <summary>去空白/引号/序号，丢掉过长过短，去重并截到 <see cref="MaxKeywords"/>。</summary>
    private static List<string> Clean(IEnumerable<string> words)
    {
        var list = new List<string>();
        foreach (var raw in words)
        {
            var w = (raw ?? "").Trim().Trim('"', '\'', '「', '」', '『', '』', '、', ',', '，', '。', '.', ' ');
            if (w.Length is < 2 or > 12) continue;
            if (list.Contains(w, StringComparer.Ordinal)) continue;
            list.Add(w);
            if (list.Count >= MaxKeywords) break;
        }
        return list;
    }
}

/// <summary>
/// 主题监听的优先队列（V4.1）：**命中多的会话多扫、命中少的少扫**。
/// 纯函数（输入命中列表 → 输出排序 + 深扫标记），便于单测，也便于把队列原样打进任务日志给用户看。
/// </summary>
public static class TopicPriorityQueue
{
    /// <summary>一轮最多深扫几个会话（避免一次任务把整个库翻一遍）。</summary>
    public const int DefaultDeepCap = 3;

    /// <summary>本周期命中数低于这个值的会话不值得深扫（只保留广扫命中的几条）。</summary>
    public const int DefaultMinDeepHits = 2;

    /// <summary>
    /// 按会话聚合命中数 → 排序（本周期命中 + 历史累计作为分数）→ 给热点会话打"深扫"标记。
    /// 历史分只用来**在同分时排先后**：老聊某主题的群即使这轮只命中一条，也优先深扫。
    /// </summary>
    public static IReadOnlyList<TopicRank> Rank(
        IEnumerable<TopicHit> hits,
        IReadOnlyDictionary<string, int>? history = null,
        int deepCap = DefaultDeepCap,
        int minDeepHits = DefaultMinDeepHits)
    {
        var bySession = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var h in hits)
        {
            if (string.IsNullOrWhiteSpace(h.SessionId)) continue;
            bySession[h.SessionId] = bySession.TryGetValue(h.SessionId, out var n) ? n + 1 : 1;
        }

        var ordered = bySession
            .Select(kv => new TopicRank(
                kv.Key,
                kv.Value,
                kv.Value + (history is not null && history.TryGetValue(kv.Key, out var past) ? past : 0),
                DeepScan: false))
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Hits)
            .ThenBy(r => r.SessionId, StringComparer.Ordinal)
            .ToList();

        var deep = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (deep >= deepCap) break;
            if (ordered[i].Hits < minDeepHits) continue;
            ordered[i] = ordered[i] with { DeepScan = true };
            deep++;
        }
        return ordered;
    }
}

/// <summary>
/// 主题监听的长期画像（V4.1）：模型扩出来的相关词 + 各会话命中累计。
/// 挂在任务上持久化，于是"这个群常聊就业"这种判断能跨轮次、跨重启保留下来——
/// 这就是"自主建立优先队列"里的"自主"：不用人喂，它自己攒。
/// </summary>
public sealed class TopicWatchProfile
{
    /// <summary>画像最多记几个会话（防止字典无限增长）。</summary>
    private const int MaxSessions = 30;

    public string Topic { get; set; } = "";
    public List<string> Keywords { get; set; } = [];
    public Dictionary<string, int> SessionHits { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset? ExpandedAt { get; set; }

    /// <summary>解析画像 JSON；坏数据一律当作"空画像"（重头学，不报错）。</summary>
    public static TopicWatchProfile Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new TopicWatchProfile();
        try
        {
            var p = JsonSerializer.Deserialize<TopicWatchProfile>(json) ?? new TopicWatchProfile();
            p.Keywords ??= [];
            p.SessionHits ??= new Dictionary<string, int>(StringComparer.Ordinal);
            return p;
        }
        catch (JsonException)
        {
            return new TopicWatchProfile();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>关键词要不要重新扩：没扩过 / 扩过但超过 TTL（相关词会随季节变，比如"秋招"→"春招"）。</summary>
    public bool NeedsKeywords(DateTimeOffset now, TimeSpan ttl)
        => Keywords.Count == 0 || ExpandedAt is null || now - ExpandedAt.Value >= ttl;

    /// <summary>把本轮优先队列并进画像（累加命中分，只留分数最高的前 <see cref="MaxSessions"/> 个会话）。</summary>
    public void Learn(IEnumerable<TopicRank> ranks)
    {
        foreach (var r in ranks)
            SessionHits[r.SessionId] = SessionHits.TryGetValue(r.SessionId, out var old) ? old + r.Hits : r.Hits;

        if (SessionHits.Count <= MaxSessions) return;

        var keep = SessionHits.OrderByDescending(kv => kv.Value).Take(MaxSessions).ToList();
        SessionHits = keep.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }
}
