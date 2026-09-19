using System.Text.Json;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Skills;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.Logging;
using MemoryAssistant.Core.Missions;
using MemoryAssistant.Core.PythonBridge;

namespace MemoryAssistant.Infrastructure.Missions;

/// <summary>一次主题扫描的结果（探测器与 agent 工具共用）。</summary>
public sealed record TopicScanOutcome(
    string Topic,
    IReadOnlyList<string> Keywords,
    bool KeywordsFromModel,
    IReadOnlyList<TopicRank> Queue,
    IReadOnlyList<TopicHit> Hits,
    string Note,
    string Context,
    string ProfileJson);

/// <summary>
/// 主题扫描（V4.1）：把"留意某个主题"落成可执行的扫描策略——
/// 1) **模型自己扩词**：主题"就业信息" → 校招/内推/招聘/秋招（聊天里真能搜到的说法）；
/// 2) **广扫**：每个相关词跨会话检索一次，按会话聚合命中数；
/// 3) **自主优先队列**：命中多的会话多扫（深读新消息），命中少的只留广扫那几条；
/// 4) 画像（相关词 + 会话命中累计）挂在任务上持久化，于是"哪个群常聊这个"能跨轮次攒下来。
///
/// 为什么不做"字形匹配"就完事：用户说的主题词（就业信息）在聊天里往往一个字都不出现，
/// 而真正相关的说法（校招、内推）是模型才能想出来的——这正是"B：让模型自己长脑子"。
/// </summary>
public sealed class TopicScanService(
    IPythonBridge bridge,
    IChatClient? chat,
    AgentOptions options,
    IAppLogger logger)
{
    /// <summary>广扫：每个相关词取多少条。</summary>
    private int WideLimit => options.EcoMode ? 15 : 50;

    /// <summary>深扫：热点会话一次读多少条新消息。</summary>
    private int DeepReadLimit => options.EcoMode ? 20 : 200;

    /// <summary>一轮最多深扫几个会话。</summary>
    private int DeepCap => options.EcoMode ? 1 : TopicPriorityQueue.DefaultDeepCap;

    /// <summary>交给任务执行器的最多行数（相关词命中）。</summary>
    private const int MaxKeywordLines = 20;

    /// <summary>深扫补充的最多行数（热点群的其它新消息，可能不含相关词）。</summary>
    private const int MaxDeepLines = 10;

    /// <summary>深扫命中的标记（与"相关词命中"区分开，上下文里分段给出）。</summary>
    private const string DeepKeyword = "深扫";

    /// <summary>相关词的保鲜期：季节会变（秋招→春招），过期就重新扩一次。</summary>
    private static readonly TimeSpan KeywordTtl = TimeSpan.FromHours(12);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// 扫一轮：<paramref name="sinceUnix"/> 是"上次执行时间"（0 = 首次，建立基线）。
    /// 返回命中、优先队列、给用户看的说明，以及要写回任务的新画像。
    /// </summary>
    public async Task<TopicScanOutcome> ScanAsync(
        string topic,
        string? profileJson,
        long sinceUnix,
        CancellationToken ct)
    {
        topic = (topic ?? "").Trim();
        var profile = TopicWatchProfile.Parse(profileJson);
        if (!string.Equals(profile.Topic, topic, StringComparison.Ordinal))
            profile = new TopicWatchProfile { Topic = topic };    // 主题换了就重学，别串味

        // 1) 相关词：模型自己扩；画像里没过期就直接复用（省一次调用）
        var fromModel = false;
        if (profile.NeedsKeywords(DateTimeOffset.Now, KeywordTtl))
        {
            var expanded = await ExpandKeywordsAsync(topic, ct);
            fromModel = expanded.FromModel;
            profile.Keywords = expanded.Keywords;
            profile.ExpandedAt = DateTimeOffset.Now;
        }
        var keywords = profile.Keywords.Count > 0 ? profile.Keywords : [topic];

        // 2) 广扫：每个相关词跨会话检索一次
        var hits = new List<TopicHit>();
        foreach (var kw in keywords)
        {
            ct.ThrowIfCancellationRequested();
            var resp = await bridge.RequestAsync("search_messages", new Dictionary<string, object?>
            {
                ["keyword"] = kw,
                ["limit"] = WideLimit,
                ["begin"] = sinceUnix,
            }, timeoutSeconds: 30, ct: ct);

            if (!resp.Success || resp.Data is not JsonElement arr || arr.ValueKind != JsonValueKind.Array)
            {
                logger.Warn($"[TopicScan] 检索「{kw}」失败：{resp.Error ?? "返回格式异常"}");
                continue;
            }

            foreach (var m in arr.EnumerateArray())
            {
                if (IsSelf(m)) continue;
                var content = ContentOf(m);
                if (content.Length == 0 || ContentNoiseFilter.IsNoise(content)) continue;
                hits.Add(new TopicHit(SessionOf(m), TimeOf(m), WhoOf(m), content, kw));
            }
        }

        // 3) 优先队列 + 热点深扫
        var queue = TopicPriorityQueue.Rank(hits, profile.SessionHits, DeepCap);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rank in queue.Where(r => r.DeepScan))
        {
            ct.ThrowIfCancellationRequested();
            var added = await DeepReadAsync(rank.SessionId, sinceUnix, names, ct);
            hits.AddRange(added);
        }

        hits = Dedup(hits);
        profile.Learn(queue);

        var note = BuildNote(topic, keywords, fromModel, queue, names, hits.Count);
        var context = BuildContext(hits, names);
        logger.Info($"[TopicScan] {note}");

        return new TopicScanOutcome(
            topic,
            keywords,
            fromModel,
            queue,
            hits,
            note,
            context,
            profile.ToJson());
    }

    /// <summary>
    /// 让模型把主题扩成"聊天里真能搜到的说法"。零 LLM（EcoMode）/无 Chat/模型抽风时退回主题本身，
    /// 保证扫描永远跑得下去（只是退化成字面检索）。
    /// </summary>
    private async Task<(List<string> Keywords, bool FromModel)> ExpandKeywordsAsync(string topic, CancellationToken ct)
    {
        if (chat is null || options.EcoMode)
        {
            logger.Info($"[TopicScan] 跳过扩词（{(chat is null ? "无模型" : "EcoMode 零 LLM")}），按字面词「{topic}」检索");
            return ([topic], false);
        }

        try
        {
            var messages = new List<ChatMessage>
            {
                new() { Role = "system", Content = TopicKeywords.SystemPrompt },
                new() { Role = "user", Content = $"主题：{topic}\n\n请给出相关词 JSON。" },
            };
            var result = await chat.ChatAsync(messages, tools: null, ct);
            var list = TopicKeywords.Parse(result.Content, topic);
            // 模型扩出来的词里不含主题本身也没关系；但若一个都没认出来，Parse 已退回主题
            logger.Info($"[TopicScan] 主题「{topic}」→ 相关词：{string.Join("/", list)}（{result.CompletionTokens} tokens）");
            return (list, true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Warn($"[TopicScan] 扩词失败，退回字面词「{topic}」：{ex.Message}");
            return ([topic], false);
        }
    }

    /// <summary>深扫：热点会话把"自上次执行以来的新消息"整段读出来（关键词没覆盖到的也算）。</summary>
    private async Task<List<TopicHit>> DeepReadAsync(
        string sessionId, long sinceUnix, Dictionary<string, string> names, CancellationToken ct)
    {
        var added = new List<TopicHit>();
        var resp = await bridge.RequestAsync("read_messages", new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
            ["limit"] = DeepReadLimit,
            ["begin"] = sinceUnix,
        }, timeoutSeconds: 30, ct: ct);

        if (!resp.Success || resp.Data is not JsonElement arr || arr.ValueKind != JsonValueKind.Array)
        {
            logger.Warn($"[TopicScan] 深扫会话 {sessionId} 失败：{resp.Error ?? "返回格式异常"}");
            return added;
        }

        foreach (var m in arr.EnumerateArray())
        {
            var sessionName = Str(m, "session_name");
            if (sessionName.Length > 0) names[sessionId] = sessionName;

            if (IsSelf(m)) continue;
            var content = ContentOf(m);
            if (content.Length == 0 || ContentNoiseFilter.IsNoise(content)) continue;
            added.Add(new TopicHit(sessionId, TimeOf(m), WhoOf(m), content, DeepKeyword));
        }
        return added;
    }

    /// <summary>同一会话同一秒同内容只留一条（广扫与深扫会重叠）。</summary>
    private static List<TopicHit> Dedup(List<TopicHit> hits)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<TopicHit>();
        foreach (var h in hits.OrderByDescending(h => h.At))
        {
            if (!seen.Add($"{h.SessionId}|{h.At}|{h.Content}")) continue;
            list.Add(h);
        }
        return list;
    }

    private static string BuildNote(
        string topic,
        IReadOnlyList<string> keywords,
        bool fromModel,
        IReadOnlyList<TopicRank> queue,
        IReadOnlyDictionary<string, string> names,
        int hitCount)
    {
        var kw = $"相关词：{string.Join("/", keywords)}{(fromModel ? "（模型扩词）" : "（字面词）")}";
        if (queue.Count == 0) return $"提到「{topic}」的新消息 0 条｜{kw}";

        var ranked = queue.Take(5).Select(r =>
        {
            var name = names.TryGetValue(r.SessionId, out var n) ? n : Short(r.SessionId);
            return $"{name}({r.Hits}{(r.DeepScan ? "·深扫" : "")})";
        });
        return $"提到「{topic}」的新消息 {hitCount} 条｜{kw}｜优先队列：{string.Join(" > ", ranked)}";
    }

    /// <summary>
    /// 组装增量上下文：**相关词命中排在前面**，深扫补充放后面单独一段。
    ///
    /// 为什么必须分段：深扫是"整段读热点群里最近的所有消息"，里面绝大多数是与主题无关的闲聊。
    /// 实测把它们和关键词命中混在一起、只按时间倒序取 30 行时，热点群的闲聊会把关键词命中全挤掉，
    /// 模型看完就回一句"本批内容与就业信息无关"——明明搜到了 241 条相关消息却等于没扫。
    /// </summary>
    private static string BuildContext(IReadOnlyList<TopicHit> hits, IReadOnlyDictionary<string, string> names)
    {
        var keyword = hits
            .Where(h => h.Keyword != DeepKeyword)
            .OrderByDescending(h => h.At)
            .Take(MaxKeywordLines)
            .Select(h => FormatLine(h, names, withKeyword: true))
            .ToList();

        var deep = hits
            .Where(h => h.Keyword == DeepKeyword)
            .OrderByDescending(h => h.At)
            .Take(MaxDeepLines)
            .Select(h => FormatLine(h, names, withKeyword: false))
            .ToList();

        var sb = new System.Text.StringBuilder();
        sb.Append("【相关词命中】\n");
        sb.Append(keyword.Count > 0 ? string.Join("\n", keyword) : "（无）");
        if (deep.Count > 0)
        {
            sb.Append("\n【热点会话补充（命中多的群多扫，可能不含相关词）】\n");
            sb.Append(string.Join("\n", deep));
        }
        return sb.ToString();
    }

    private static string FormatLine(TopicHit h, IReadOnlyDictionary<string, string> names, bool withKeyword)
    {
        var when = h.At > 0
            ? DateTimeOffset.FromUnixTimeSeconds(h.At).ToLocalTime().ToString("MM-dd HH:mm")
            : "--";
        var session = names.TryGetValue(h.SessionId, out var n) ? n : Short(h.SessionId);
        var kw = withKeyword && h.Keyword.Length > 0 ? $"[{h.Keyword}]" : "";
        return $"[{when}] {session} · {h.Who}: {kw}{h.Content}";
    }

    private static string Short(string sessionId)
        => sessionId.Length <= 12 ? sessionId : sessionId[..12] + "…";

    private static bool IsSelf(JsonElement message)
    {
        if (!message.TryGetProperty("is_self", out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => v.GetInt64() != 0,
            JsonValueKind.String => v.GetString() is "1" or "true",
            _ => false,
        };
    }

    private static string ContentOf(JsonElement m) => Str(m, "content");

    private static string WhoOf(JsonElement m)
    {
        var name = Str(m, "display_name");
        return name.Length > 0 ? name : Str(m, "sender");
    }

    private static string SessionOf(JsonElement m) => Str(m, "session_id");

    private static long TimeOf(JsonElement m)
        => m.TryGetProperty("create_time", out var t) && t.TryGetInt64(out var sec) ? sec : 0;

    private static string Str(JsonElement m, string name)
        => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
