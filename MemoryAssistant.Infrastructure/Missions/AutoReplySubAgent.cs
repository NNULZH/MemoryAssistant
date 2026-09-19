using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Skills;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.Integrations;
using MemoryAssistant.Core.Logging;
using MemoryAssistant.Core.Missions;
using MemoryAssistant.Core.PythonBridge;

namespace MemoryAssistant.Infrastructure.Missions;

/// <summary>
/// 回复子智能体交回的"关键信息"（只回关键结论，不回传原始聊天记录）。
/// 主对话/任务日志只消费这一个对象：既避免把几百条聊天灌进主上下文，
/// 也让"发了没有、发给谁、发了什么、为什么没发"一目了然。
/// </summary>
public sealed record ReplyOutcome
{
    /// <summary>是否真的发出去了。</summary>
    public bool Sent { get; init; }
    /// <summary>本次没有发送（无新消息 / 模型认为不必回 / 被安全闸门拦下）。</summary>
    public bool Skipped { get; init; }
    /// <summary>是否已经生成了回复正文（即使没发出去）。</summary>
    public bool Generated { get; init; }
    public string Target { get; init; } = "";
    /// <summary>实际发送的正文（未发送时为空）。</summary>
    public string Text { get; init; } = "";
    /// <summary>生成了但没发送的草稿（供用户查看/手动发）。</summary>
    public string Draft { get; init; } = "";
    /// <summary>一句话关键信息（写进任务"上次结果"）。</summary>
    public string Note { get; init; } = "";
    /// <summary>本次抓到的"对方新消息"条数。</summary>
    public int NewCount { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// 自动回复子智能体（V3.7）：把"盯着某个会话"变成"真的替你回一句"。
///
/// 它只做四件事，每件都确定性：
///   1. 抓最新记录（<c>read_messages</c>）→ 自己挑出"对方发来的新消息"（丢掉自己发的与系统噪音）；
///   2. 把最近对话 + 新消息交给模型，只生成一条可直接发送的正文（模型说 SKIP 就不发）；
///   3. 走现有写操作桥 <see cref="IWeChatActionBridge"/>：**先确认打开了目标会话，再发送**；
///   4. 返回 <see cref="ReplyOutcome"/>（关键信息），原始聊天上下文不外传。
///
/// 安全边界（写操作，必须比只读严格）：
///   - 没有明确回复对象 → 一律不发（否则可能发到"当前恰好打开的会话"）；
///   - 微信窗口找不到 / 写桥未接入 → 不发，如实说明；
///   - **首次运行只建立基线**，绝不回复历史消息（避免刚建任务就把陈年消息回一遍）；
///   - 打开目标会话失败 → 直接放弃，绝不"对着当前焦点"发送；
///   - <c>EcoMode</c>（验收/自检模式）与 <c>SuppressAutoReply</c>（全局刹车）下只出草稿、不发送；
///   - 每次执行最多回复一条，且只回复"比上次执行更新"的消息，不会重复回、不会回自己。
/// </summary>
public sealed class AutoReplySubAgent
{
    /// <summary>喂给模型的最近对话条数（既是上下文，也是"新消息"的取样范围）。</summary>
    private const int RecentContextMessages = 20;
    /// <summary>回复正文上限：微信里太长就不像人说的话了。</summary>
    private const int MaxReplyChars = 200;

    private static readonly Regex ReplyPrefix = new(@"^(?:回复|答复)\s*[：:]\s*", RegexOptions.Compiled);
    private static readonly Regex MultiSpace = new(@"\s{2,}", RegexOptions.Compiled);

    private readonly IPythonBridge _bridge;
    private readonly IChatClient? _chat;
    private readonly IWeChatActionBridge? _action;
    private readonly AgentOptions _options;
    private readonly IAppLogger? _logger;

    public AutoReplySubAgent(
        IPythonBridge bridge,
        IChatClient? chat,
        IWeChatActionBridge? action,
        AgentOptions options,
        IAppLogger? logger = null)
    {
        _bridge = bridge;
        _chat = chat;
        _action = action;
        _options = options;
        _logger = logger;
    }

    public async Task<ReplyOutcome> RunAsync(MissionDefinition mission, CancellationToken ct)
    {
        var target = (mission.Target ?? "").Trim();
        if (target.Length == 0)
            return NotSent("", "", 0, "未指定回复对象，未发送任何消息");

        if (_chat is null)
            return NotSent(target, "", 0, "未配置模型（LLM），无法生成回复，未发送");

        if (_action is null)
            return NotSent(target, "", 0, "微信写操作未接入（当前环境没有动作桥），未发送");

        if (!_action.IsAvailable)
            return NotSent(target, "", 0, "未找到微信窗口（请先登录并打开微信），未发送");

        // 首次运行：只把"现在"记为基线。否则刚建完任务就会把历史消息回一遍。
        if (mission.LastRunAt is null)
            return new ReplyOutcome
            {
                Skipped = true,
                Target = target,
                Note = "首次运行：已记录当前进度作为基线，此后只回复新收到的消息",
            };

        var sessionId = await ResolveSessionAsync(target, ct);
        if (sessionId.Length == 0)
            return NotSent(target, "", 0, $"未能解析到会话「{target}」，未发送");

        var read = await ReadRecentAsync(sessionId, ct);
        if (read.Error is not null) return NotSent(target, "", 0, read.Error);

        var since = mission.LastRunAt.Value.ToUnixTimeSeconds();
        var incoming = read.Messages.Where(m => m.CreateTime > since && !m.IsSelf && !IsNoise(m.Content)).ToList();
        if (incoming.Count == 0)
            return new ReplyOutcome
            {
                Skipped = true,
                Target = target,
                Note = $"「{target}」没有对方的新消息，未回复",
            };

        var draft = await ComposeAsync(mission, target, read.Messages, incoming, ct);
        if (draft is null)
            return new ReplyOutcome
            {
                Skipped = true,
                Target = target,
                NewCount = incoming.Count,
                Note = $"「{target}」有 {incoming.Count} 条新消息，但没生成出可发送的回复（模型失败/判断不必回），未发送",
            };

        // 只出草稿、不发送的两个闸门：验收/Eco 模式（避免自检真发消息）与全局刹车。
        var holdReason = _options.EcoMode ? "验收/Eco 模式" : _options.SuppressAutoReply ? "自动回复已全局关闭" : null;
        if (holdReason is not null)
        {
            _logger?.Info($"[AutoReply] {holdReason}：只生成草稿，未发送 → {draft}");
            return new ReplyOutcome
            {
                Generated = true,
                Target = target,
                Draft = draft,
                NewCount = incoming.Count,
                Note = $"{holdReason}：未发送，草稿「{draft}」",
            };
        }

        // 必须先打开目标会话成功才发送：绝不"对着当前焦点窗口"发出去。
        // 会话类型已经从 sessionId 解析出来了（群聊 id 以 @chatroom 结尾），顺手传给桥：
        // 否则搜索下拉里"联系人"分区可能先命中一个同名的人，把回复发错对象。
        var kind = sessionId.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase)
            ? ChatTargetKind.Group
            : ChatTargetKind.Contact;
        var opened = await _action.OpenChatAsync(target, kind, ct);
        if (!opened.Success)
        {
            _logger?.Warn($"[AutoReply] 打开「{target}」失败，已放弃发送：{opened.Error}");
            return new ReplyOutcome
            {
                Generated = true,
                Target = target,
                Draft = draft,
                NewCount = incoming.Count,
                Error = opened.Error,
                Note = $"已生成草稿但未发送（打开「{target}」失败：{opened.Error}）",
            };
        }

        var sent = await _action.SendMessageAsync(draft, ct);
        if (!sent.Success)
        {
            _logger?.Warn($"[AutoReply] 向「{target}」发送失败：{sent.Error}");
            return new ReplyOutcome
            {
                Generated = true,
                Target = target,
                Draft = draft,
                NewCount = incoming.Count,
                Error = sent.Error,
                Note = $"已生成草稿但发送失败（{sent.Error}）",
            };
        }

        _logger?.Info($"[AutoReply] 已向「{target}」自动回复（{incoming.Count} 条新消息）：{draft}");
        return new ReplyOutcome
        {
            Sent = true,
            Generated = true,
            Target = target,
            Text = draft,
            NewCount = incoming.Count,
            Note = $"已自动回复「{target}」（{incoming.Count} 条新消息）：{draft}",
        };
    }

    // ---------- 抓最新记录 ----------

    private readonly record struct RecentMessage(long CreateTime, bool IsSelf, string Who, string Content);

    private sealed record RecentRead(IReadOnlyList<RecentMessage> Messages, string? Error);

    /// <summary>
    /// 读最近若干条消息：既做"模型能看懂的上下文"，也用来挑出比上次执行更新的、对方发来的消息。
    /// 注意 read_messages 是"从最新往回读"，所以这里拿到的是最近 N 条（升序整理后交给模型）。
    /// </summary>
    private async Task<RecentRead> ReadRecentAsync(string sessionId, CancellationToken ct)
    {
        var resp = await _bridge.RequestAsync("read_messages", new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
            ["limit"] = RecentContextMessages,
        }, timeoutSeconds: 60, ct: ct);

        if (!resp.Success || resp.Data is not JsonElement arr || arr.ValueKind != JsonValueKind.Array)
            return new RecentRead([], $"读取会话消息失败（{resp.Error ?? "未知错误"}），未发送");

        var list = new List<RecentMessage>();
        foreach (var m in arr.EnumerateArray())
        {
            var content = m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            var who = m.TryGetProperty("display_name", out var d) ? d.GetString() ?? "" : "";
            var ts = m.TryGetProperty("create_time", out var t) && t.TryGetInt64(out var sec) ? sec : 0;
            list.Add(new RecentMessage(ts, IsSelf(m), who, content));
        }
        list.Sort((a, b) => a.CreateTime.CompareTo(b.CreateTime));
        return new RecentRead(list, null);
    }

    /// <summary>回复对象（口语名）→ 会话 id；顺带兼容"文件传输助手"这类库里存英文 id 的会话。</summary>
    private async Task<string> ResolveSessionAsync(string target, CancellationToken ct)
    {
        var keyword = MissionTargetAliases.Resolve(target);
        // ⚠ 必须用 find_sessions（按备注名/昵称/群名找会话），**不能用 list_sessions 的关键词**：
        // 后者只匹配 wxid / 会话摘要 / 最后发言人，同一个词会命中"她恰好发言过的那个群"。
        // 实测：自动回复任务每轮都回"未能解析到会话「张晓明」，未发送"，一条都没发出去过——
        // 就是因为这里走的是 list_sessions。
        var resp = await _bridge.RequestAsync("find_sessions", new Dictionary<string, object?>
        {
            ["keyword"] = keyword,
            ["private_only"] = null,      // 不限定私聊（追踪对象也可能是群）
            ["limit"] = 8,
        }, timeoutSeconds: 30, ct: ct);

        if (!resp.Success || resp.Data is not JsonElement arr || arr.ValueKind != JsonValueKind.Array)
        {
            _logger?.Warn($"[自动回复] find_sessions 失败：{resp.Error}");
            return "";
        }

        // 名字完全相等优先（find_sessions 是模糊匹配，同一个词常横跨多个会话）；
        // 没有精确命中就退"最近活跃的那个"，但把实际选中的名字记下来，便于事后核对。
        var fallback = "";
        var fallbackName = "";
        long fallbackTs = long.MinValue;
        foreach (var s in arr.EnumerateArray())
        {
            var user = s.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
            if (user.Length == 0) continue;
            var name = s.TryGetProperty("display_name", out var d) ? d.GetString() ?? "" : "";
            var ts = s.TryGetProperty("last_timestamp", out var t) && t.TryGetInt64(out var v) ? v : 0;

            // 精确命中：会话名相同，或 username 就是关键词本身
            // （"文件传输助手"这类会被别名解析成英文 id：filehelper，此时靠 username 命中）
            if (string.Equals(name.Trim(), keyword.Trim(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(user.Trim(), keyword.Trim(), StringComparison.OrdinalIgnoreCase))
                return user;

            // 退而求其次也只认"名字里确实含这个词"的候选：
            // 自动回复是写操作，宁可回"未能解析到会话"（不发），也不能赌一个名字对不上的会话——
            // 发错人比不发严重得多。
            var nameMatches = name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                              || keyword.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase);
            if (!nameMatches || name.Trim().Length == 0) continue;
            if (ts > fallbackTs) { fallback = user; fallbackName = name; fallbackTs = ts; }
        }

        if (fallback.Length > 0)
            _logger?.Info($"[自动回复] 「{target}」没有精确命中的会话，退用最近活跃的「{fallbackName}」");
        return fallback;
    }

    // ---------- 生成回复 ----------

    private async Task<string?> ComposeAsync(
        MissionDefinition mission, string target,
        IReadOnlyList<RecentMessage> recent, IReadOnlyList<RecentMessage> incoming, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = SystemPrompt },
            new() { Role = "user", Content = BuildUserPrompt(mission, target, recent, incoming) },
        };

        try
        {
            var result = await _chat!.ChatAsync(messages, tools: null, ct);
            var text = CleanReply(result.Content);
            if (text is null)
                _logger?.Info($"[AutoReply] 模型判断无需回复或输出为空（{result.CompletionTokens} tokens）");
            return text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 生成失败绝不退化成"发一句模板"——宁可不发。
            _logger?.Warn($"[AutoReply] 生成回复失败，未发送：{ex.Message}");
            return null;
        }
    }

    /// <summary>模型输出的清洗：去掉前缀/引号/换行，超长截断。返回 null = 不该发。</summary>
    public static string? CleanReply(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        if (text.Equals("SKIP", StringComparison.OrdinalIgnoreCase)) return null;

        text = text.Replace("\r", " ").Replace("\n", " ");
        text = ReplyPrefix.Replace(text, "");
        text = text.Trim().Trim('"', '\'', '“', '”', '‘', '’', '「', '」', '《', '》', ' ', '　');
        text = MultiSpace.Replace(text, " ").Trim();
        if (text.Length == 0) return null;
        return text.Length <= MaxReplyChars ? text : text[..MaxReplyChars];
    }

    private const string SystemPrompt = """
你在替用户回复微信消息。用户会给你最近的聊天记录和"对方刚发来的新消息"，你只输出一条可以直接发出去的回复正文。

硬性要求：
1. 只输出回复正文本身：不要解释、不要加引号、不要写"回复："这类前缀、不要换行、不要 @ 对方、不要署名。
2. 像用户本人在打字：短、口语、自然，一般 1~2 句、最多 100 字。
3. 只依据给出的聊天记录作答，绝不编造没提到的事情、时间、地点、金额或承诺。
4. 不要替用户许下他没说过的具体承诺（例如"我明天一定给你"）；拿不准就说你会确认一下。
5. 不要暴露你是 AI，也不要说"我收到你的消息了""我看到了你的消息"这类机器人话术。
6. 如果对方的消息不需要回复（广告、系统通知、群发、纯图片/表情、无需回应），只输出：SKIP
""";

    private static string BuildUserPrompt(
        MissionDefinition mission, string target,
        IReadOnlyList<RecentMessage> recent, IReadOnlyList<RecentMessage> incoming)
    {
        var sb = new StringBuilder();
        sb.Append("【回复对象】").Append(target).Append('\n');
        sb.Append("【用户对这个任务的要求】").Append(mission.Goal).Append('\n');

        sb.Append("\n【最近对话（越靠后越新）】\n");
        foreach (var m in recent)
            sb.Append('[').Append(Format(m.CreateTime)).Append("] ")
              .Append(m.IsSelf ? "我" : (string.IsNullOrWhiteSpace(m.Who) ? "对方" : m.Who))
              .Append(": ").Append(Clamp(m.Content, 200)).Append('\n');

        sb.Append("\n【对方刚发来的新消息（要回的就是这些）】\n");
        foreach (var m in incoming)
            sb.Append('[').Append(Format(m.CreateTime)).Append("] ")
              .Append(string.IsNullOrWhiteSpace(m.Who) ? "对方" : m.Who)
              .Append(": ").Append(Clamp(m.Content, 200)).Append('\n');

        sb.Append("\n只输出要发送的回复正文（不需要回复就输出 SKIP）。");
        return sb.ToString();
    }

    // ---------- 小工具 ----------

    private static string Format(long unixSeconds)
        => unixSeconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().ToString("MM-dd HH:mm") : "--";

    private static string Clamp(string s, int max)
    {
        var t = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }

    /// <summary>
    /// 回复场景不能直接照搬"证据噪音过滤"：那条规则会把 1~2 个字的真人消息（"在吗""好的"）
    /// 也当噪音丢掉，结果就是"对方明明说话了却不回"。这里只挡空内容与真正的系统提示/纯媒体，
    /// 短消息交给模型判断要不要回。
    /// </summary>
    private static bool IsNoise(string content)
    {
        var t = content.Trim();
        if (t.Length == 0) return true;
        return t.Length > 2 && ContentNoiseFilter.IsNoise(t);
    }

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

    private static ReplyOutcome NotSent(string target, string draft, int newCount, string note)
        => new() { Target = target, Draft = draft, NewCount = newCount, Note = note };
}
