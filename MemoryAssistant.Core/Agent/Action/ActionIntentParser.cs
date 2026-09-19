using System.Text.RegularExpressions;

namespace MemoryAssistant.Core.Agent.Action;

/// <summary>解析出的写操作意图。</summary>
public sealed record ParsedAction
{
    /// <summary>open_chat | send_message。</summary>
    public string Action { get; init; } = "";
    /// <summary>目标会话（人名/群名）；回复当前会话时为空。</summary>
    public string Target { get; init; } = "";
    /// <summary>要发送的文本；只打开会话时为空。</summary>
    public string Payload { get; init; } = "";
}

/// <summary>
/// 动作指令解析（V3.4，规则版）：把"打开X的聊天""给X发消息：Y""回复：Y"转成结构化写操作。
/// 只认显式祈使句，避免把"看看和X的聊天"这类只读请求误判成写操作（写操作是有副作用的）。
/// </summary>
public static class ActionIntentParser
{
    // 打开 / 打开和 / 打开跟 / 打开与 → X（的）聊天|会话|对话
    private static readonly Regex OpenChat = new(
        @"^打开(?:和|跟|与)?(?<p>.{1,20}?)(?:的)?(?:聊天|会话|对话)", RegexOptions.Compiled);

    // 发送类：必须能明确看出"要发给谁、要说什么"，否则宁可不动手（写操作有副作用，误判=发错人）。
    // 1) 给X发(送)(一条)(消息|微信|私信)[：,，]? 内容 —— 有"发消息"这类明确动词，冒号可省
    private static readonly Regex SendToMsg = new(
        @"^给(?<p>.{1,20}?)发(?:送)?(?:一条|个)?(?:消息|微信|私信)[：:，,]?\s*(?<t>.*)$", RegexOptions.Compiled);

    // 2) 给X发(送)(一条|个)[：]内容 —— 省掉"消息"二字时**必须带冒号**，
    //    否则"我给张晓明发了个红包"会被当成"给张晓明发送：了个红包"（真的会发出去）
    private static readonly Regex SendToBrief = new(
        @"^给(?<p>.{1,20}?)发(?:送)?(?:一条|个)?[：:]\s*(?<t>.+)$", RegexOptions.Compiled);

    // 3) 跟/和/与/对X说(一声|一句|一下)[：]内容 —— 必须带冒号：
    //    否则"我跟张晓明说过这件事"会命中，把"过这件事"当成要发送的内容
    private static readonly Regex SayTo = new(
        @"^(?:跟|和|与|对)(?<p>.{1,20}?)(?:说|讲)(?:一声|一句|一下)?[：:]\s*(?<t>.+)$", RegexOptions.Compiled);

    // 4) 告诉X[：]内容 —— 同样必须带冒号
    private static readonly Regex TellTo = new(
        @"^告诉(?<p>.{1,20}?)[：:]\s*(?<t>.+)$", RegexOptions.Compiled);

    // 5) 发(一条)?消息给X[：,，]? 内容 —— 明确动词，冒号可省。
    //    注意尾巴必须"要么跟分隔符、要么结束"：否则惰性 p 只吃一个字就满足 `.*`，
    //    "发消息给张三：晚点到"会被解析成 目标=张 / 内容=三：晚点到
    private static readonly Regex SendMsgTo = new(
        @"^发(?:送)?(?:一条|个)?(?:消息|微信|私信)给(?<p>[^：:，,\s]{1,20})(?:[：:，,]\s*(?<t>.+))?$",
        RegexOptions.Compiled);

    // 6) 给X发消息[，,]内容是Y —— 口语里最常见的说法之一（"帮我给张三发消息，内容是明天见"）
    private static readonly Regex SendContentIs = new(
        @"^给(?<p>.{1,20}?)发(?:送)?(?:一条|个)?(?:消息|微信|私信)?[，,]?\s*内容(?:是|为|就是)[：:]?\s*(?<t>.+)$",
        RegexOptions.Compiled);

    // 7) 给X发一条<正文> —— 最省事的口语（实测用户就是这么说的："给张晓明发一条测试消息"）。
    //    必须排在 SendToMsg 之后：否则"给X发一条消息：明天见"会被这里吃掉，
    //    把正文解析成"消息：明天见"（多带两个字）。
    //    只认"一条/一句/一段"这类**文本量词**，不认光杆"个"：
    //    "给张晓明发了个红包"不是发文本，落到这里会真的发出去一句"红包"，宁可交给只读链路。
    private static readonly Regex SendQuantifier = new(
        @"^给(?<p>.{1,20}?)发(?:送)?(?:一条|一句|一段)(?<t>.+)$", RegexOptions.Compiled);

    /// <summary>可选的礼貌前缀："帮我给张三发消息…" 是用户最常见的说法，去掉前缀再匹配。</summary>
    private static readonly Regex PolitePrefix = new(
        @"^(?:帮我|帮忙|请|麻烦|能否|可以|能不能)\s*", RegexOptions.Compiled);

    // 回复：内容（必须有冒号，避免"回复率是多少"这类误判）
    private static readonly Regex ReplyColon = new(
        @"^回复[：:]\s*(?<t>.+)$", RegexOptions.Compiled);

    // 回复他/她/它/这个（可带逗号）内容
    private static readonly Regex ReplyPronoun = new(
        @"^回复(?:他|她|它|这个)[，,]?\s*(?<t>.+)$", RegexOptions.Compiled);

    // 8) 省略收件人的发送（追问型）：「再发送一句X」「再发一条：X」「接着发一段X」。
    //    实测最常出现的就是这句（用户先"给张晓明发消息：A"，再说"再发送一句B"）。
    //    为什么必须带"条/句/段"这类**文本量词**才认：不要求量词的话"发现这个问题"会被
    //    当成"发送：现这个问题"——那是会真的发出去的，宁可不认。
    //    收件人不在这句话里，靠传进来的 fallback（上一轮谈到的那个会话）补；
    //    没有 fallback 就**不认**：认了只能发到"当前碰巧打开的那个会话"，那是最容易发错人的情况。
    private static readonly Regex SendFollowUp = new(
        @"^(?:再|接着|继续)?(?:帮我)?(?:发送|发)(?:一条|一句|一段)(?:消息|微信|私信|内容)?[：:]?\s*(?<t>.+)$",
        RegexOptions.Compiled);

    private static readonly char[] TrimChars =
        [' ', '\u3000', '，', '。', '！', '？', ',', '.', '!', '?', '“', '”', '"', '\'', '「', '」', '【', '】', '『', '』'];

    /// <summary>规划器/用户常写成"微信「张三」"这种带壳形式：只把这种**整体带括号**的壳剥掉。
    /// 不能简单 Replace("微信","")——"微信支付""微信团队"本身就是会话名。
    /// 收尾括号是可选的：OCR/转述里常只剩半边（"微信「张三"）。</summary>
    private static readonly Regex WrappedWechat = new(
        "^微信\\s*[「『【\"'\u201c\u2018](?<n>[^」』】\"'\u201d\u2019]+)[」』】\"'\u201d\u2019]?$", RegexOptions.Compiled);

    /// <summary>解析失败返回 null（交由只读链路处理）。</summary>
    public static ParsedAction? TryParse(string? query)
    {
        var text = PolitePrefix.Replace((query ?? "").Trim(), "", 1).Trim();
        if (text.Length == 0) return null;

        // 发送类：命中即"打开该会话 + 发送"（内容为空则只打开会话，不发）
        // SendContentIs 必须排在前面：它是"…发消息，内容是X"这种带说明的写法，
        // 放到后面会被 SendToMsg 先吃掉，把"内容是"也算进正文里。
        // SendQuantifier 必须排在 SendToMsg/SendToBrief 之后（见各自的注释）。
        foreach (var send in new[] { SendContentIs, SendToMsg, SendToBrief, SendQuantifier, SayTo, TellTo, SendMsgTo })
        {
            if (send.Match(text) is not { Success: true } m) continue;
            var person = CleanTarget(m.Groups["p"].Value);
            if (person.Length == 0) continue;
            var payload = Clean(m.Groups["t"].Value);
            return payload.Length > 0
                ? new ParsedAction { Action = "send_message", Target = person, Payload = payload }
                : new ParsedAction { Action = "open_chat", Target = person };
        }

        if (OpenChat.Match(text) is { Success: true } o)
        {
            var person = CleanTarget(o.Groups["p"].Value);
            return person.Length == 0
                ? null
                : new ParsedAction { Action = "open_chat", Target = person };
        }

        foreach (var reply in new[] { ReplyColon, ReplyPronoun })
        {
            if (reply.Match(text) is not { Success: true } r) continue;
            var payload = Clean(r.Groups["t"].Value);
            // Target 为空 = 发往当前已打开的会话
            return payload.Length == 0
                ? null
                : new ParsedAction { Action = "send_message", Target = "", Payload = payload };
        }

        return null;
    }

    /// <summary>
    /// 解析（带"上一轮谈到的人"）：省略收件人的追问——「再发送一句X」「再发一条：X」——
    /// 只有靠它才能补出对象。没有 lastPerson 时**不认**这类句子（见 SendFollowUp 的注释）。
    /// </summary>
    public static ParsedAction? TryParse(string? query, string? lastPerson)
    {
        var parsed = TryParse(query);
        if (parsed is not null) return parsed;

        var text = PolitePrefix.Replace((query ?? "").Trim(), "", 1).Trim();
        if (text.Length == 0) return null;
        if (SendFollowUp.Match(text) is not { Success: true } m) return null;

        var payload = Clean(m.Groups["t"].Value);
        var person = CleanTarget(lastPerson ?? "");
        if (payload.Length == 0 || person.Length == 0) return null;
        return new ParsedAction { Action = "send_message", Target = person, Payload = payload };
    }

    /// <summary>
    /// 是不是"省略收件人的发送追问"（「再发送一句X」「再发一条：X」）。
    ///
    /// 两个用处：① 规则规划器据此把这类句子路由给 action 技能（它拿得到上一轮的人，能补出收件人）；
    /// ② action 技能解析不出来时据此说清"是没说是发给谁"，而不是让作答器脑补成"没等到回执"。
    /// 判据必须**严格**（只认这条正则），不能退化成"句子里有'发送'就算"——
    /// 那会把"我最近发送过什么消息"这种只读提问也拖进写操作链路。
    /// </summary>
    public static bool IsBareSendFollowUp(string? query)
        => SendFollowUp.IsMatch(PolitePrefix.Replace((query ?? "").Trim(), "", 1).Trim());

    private static string Clean(string s) => s.Trim().Trim(TrimChars);

    /// <summary>目标名清洗：去掉引号/括号外壳，并把"微信「X」"这类整体带壳的修饰剥掉。</summary>
    private static string CleanTarget(string s)
    {
        var t = Clean(s);
        if (t.Length == 0) return t;
        if (WrappedWechat.Match(t) is { Success: true } m) t = m.Groups["n"].Value.Trim();
        // 剥壳后可能又露出引号（"微信「"张三"」"这种混写）
        return t.Trim().Trim(TrimChars);
    }
}
