using System.Text.RegularExpressions;

namespace MemoryAssistant.Core.Missions;

/// <summary>
/// 需求 → 任务草案的规则解析器（确定性，零 LLM）：
/// 判断"这句话是不是在让我长期盯着某件事"，并抽出触发方式 / 追踪对象 / 巡检间隔。
///
/// 设计原则：宁可不识别（返回 null，走普通问答），也不误把"我每天都聊天吗"这类
/// 提问当成建任务。因此要求**明确的编排动词或时间触发词**同时出现。
/// 仅支持规则可解析的说法；解析不出时由调用方诚实告知，不猜。
/// </summary>
public static class MissionIntentParser
{
    /// <summary>追踪类动词（"盯着某个会话"）。只收强动词，避免"关注"这类弱词把普通检索误判成建任务。</summary>
    private static readonly string[] TrackVerbs =
        ["追踪", "盯着", "盯住", "盯一下", "监控", "看着", "帮我盯"];

    /// <summary>
    /// 弱追踪动词（V4.1）："留意/关注"本身是陈述句里也常见的词（"我留意到他最近没说话"），
    /// 所以**只有在同句出现显式编排词（新增一个/建任务）或周期词时**才算"盯着某事"。
    /// 用户说"新增一个 loop，可以留意就业信息"正是这种组合。
    /// </summary>
    private static readonly string[] WeakTrackVerbs = ["留意", "关注"];

    /// <summary>提醒类词（要求"主动告诉我"）。</summary>
    private static readonly string[] RemindWords =
        ["提醒我", "通知我", "告诉我", "汇报给我", "总结给我", "发给我", "主动告诉"];

    /// <summary>时间触发词（周期性）。</summary>
    private static readonly string[] ScheduleWords =
        ["定时", "定期", "每隔", "每天", "每日", "每周", "每星期", "每小时", "每月", "每天晚上", "每天晚上都"];

    /// <summary>显式"建任务"说法（含"新增一个 loop"这类口语）。</summary>
    private static readonly string[] MissionWords =
        ["创建任务", "建个任务", "建一个任务", "新建任务", "编排", "自动执行", "自动帮我", "长期帮我",
         "新增一个", "新增", "加一个", "再来一个", "创建一个"];

    /// <summary>自动回复类说法（V3.7）。这是**写操作**意图：命中即要求"能明确指定回复对象"，否则不建任务（不猜）。
    /// 长词在前，避免"自动回"把"自动回复"截短。
    /// </summary>
    private static readonly string[] ReplyWords =
        ["自动回复", "自动回消息", "自动回", "帮我回复", "替我回复", "自动答"];

    /// <summary>
    /// "智能聊天/让 AI 决定回不回"这类说法（V4.1）——和 <see cref="ReplyWords"/> 同属写操作意图。
    /// 只收**复合词**：裸"聊天"会把"帮我追踪和X的聊天"误判成自动回复（那是只读的追踪任务）。
    /// </summary>
    private static readonly string[] ChatReplyWords =
        ["智能聊天", "智能回复", "自动聊天", "主动聊天", "帮我聊天", "替我聊天", "是否回复", "决定回不回"];

    /// <summary>
    /// "和/跟/与 X 智能聊天"：回复对象写在动作词**之前**（V4.1）。
    /// 动作词按"长的优先"排列，否则"和张晓明智能聊天"会被切成对象"张晓明智能"。
    /// </summary>
    private static readonly Regex TargetAfterAnd = new(
        @"(?:和|跟|与)(?<p>[^，,。；;、]{1,24}?)(?:智能聊天|智能回复|自动聊天|主动聊天|聊天|互动)",
        RegexOptions.Compiled);

    /// <summary>回复对象写在动作词之前时的形态（"张晓明发消息就自动回复"）。</summary>
    private static readonly Regex TargetBeforeReply = new(
        @"^(?<p>.{1,24}?)(?:发消息|发来消息|发来的消息|发的消息|有消息|要回)", RegexOptions.Compiled);

    /// <summary>不是"某个人"的指代，不能当回复对象（否则会建出一个回给"别人"的任务）。</summary>
    private static readonly string[] PronounTargets =
        ["别人", "人家", "对方", "他们", "她们", "大家", "某人", "这个人", "那个人"];

    private static readonly Regex EveryMinutes = new(@"每\s*(?:隔\s*)?(?<n>\d{1,4})\s*分钟", RegexOptions.Compiled);
    private static readonly Regex EveryMinutesCn = new(@"每\s*(?:隔\s*)?(?<n>[一二三四五六七八九十两]+)\s*分钟", RegexOptions.Compiled);

    /// <summary>秒级周期（V4.1）："每 30 秒 / 每30s"。数字优先，中文数字在后（EverySecondsCn）。
    /// s 后面不能跟字母数字，否则会把"每 5 samples"这类也当周期。</summary>
    private static readonly Regex EverySeconds = new(@"每\s*(?:隔\s*)?(?<n>\d{1,4})\s*(?:秒|s(?![A-Za-z0-9]))", RegexOptions.Compiled);
    private static readonly Regex EverySecondsCn = new(@"每\s*(?:隔\s*)?(?<n>[一二三四五六七八九十两]+)\s*秒", RegexOptions.Compiled);

    private static readonly string[] TargetStops =
    [
        "的聊天记录", "的聊天", "聊天记录", "的新消息", "新消息", "的消息", "发的消息", "消息记录",
        "的消息记录", "的会话", "会话", "的微信", "微信", "聊天", "里", "中", "，", ",", "。", "；", ";", " ", "、",
    ];

    /// <summary>解析需求；不是"任务编排"请求时返回 null（调用方按普通问答处理）。</summary>
    public static MissionDraft? TryParse(string? query)
    {
        var text = (query ?? "").Trim();
        if (text.Length < 4) return null;

        var explicitMission = ContainsAny(text, MissionWords);
        var remind = ContainsAny(text, RemindWords);
        var everySeconds = EverySeconds.IsMatch(text) || EverySecondsCn.IsMatch(text);
        var schedule = ScheduleWords.FirstOrDefault(w => text.Contains(w, StringComparison.Ordinal))
                       ?? (everySeconds ? "每N秒" : null);
        var trackVerb = TrackVerbs.FirstOrDefault(v => text.Contains(v, StringComparison.Ordinal));
        // 弱动词（"留意/关注"）只在与显式编排词或周期词同现时才升级成"盯着某事"（见 WeakTrackVerbs 注释）
        if (trackVerb is null && (explicitMission || schedule is not null)
            && WeakTrackVerbs.FirstOrDefault(w => text.Contains(w, StringComparison.Ordinal)) is { } weak)
            trackVerb = weak;
        // 两类说法都算"回复意图"：老的「自动回复」与新的「智能聊天/AI 决定是否回复」（V4.1）
        var replyWord = ReplyWords.FirstOrDefault(w => text.Contains(w, StringComparison.Ordinal))
                        ?? ChatReplyWords.FirstOrDefault(w => text.Contains(w, StringComparison.Ordinal));

        // 必须至少命中一类编排信号，否则这不是"建任务"，而是普通提问（如"我每天都聊天吗"）。
        if (trackVerb is null && !remind && schedule is null && !explicitMission && replyWord is null) return null;

        // 疑问句一律不是"派活"：既有的宽松判断之外，自动回复也不接受疑问句
        // （"怎么自动回复别人的消息？"不得建出一个回给"别人"的任务）。
        if (IsInterrogative(text) && (replyWord is not null ||
            (!HasImperative(text) && trackVerb is null && !explicitMission))) return null;

        var action = replyWord is not null ? MissionActionKind.AutoReply : MissionActionKind.Summarize;

        var trigger = trackVerb is not null
            ? MissionTriggerKind.Watch
            : schedule is not null
                ? MissionTriggerKind.Interval
                : MissionTriggerKind.Manual;

        // 自动回复：默认走"增量激活"（对方有新消息才动），写了周期词才改成定时扫描。
        if (action == MissionActionKind.AutoReply && schedule is null)
            trigger = MissionTriggerKind.Watch;

        var target = ExtractTarget(text, trigger, action, replyWord, trackVerb);
        // 自动回复是写操作：说不清回给谁就不建任务（宁可不识别，也不猜一个对象出来）。
        if (action == MissionActionKind.AutoReply && target.Length == 0) return null;

        var (intervalMinutes, intervalSeconds) = ResolveInterval(text, trigger);

        var snippet = Snippet(text);
        var title = action == MissionActionKind.AutoReply
            ? $"自动回复「{target}」的新消息"
            : trigger switch
            {
                MissionTriggerKind.Watch when target.Length > 0 => $"追踪「{target}」的新消息",
                MissionTriggerKind.Watch => $"常驻监听：{snippet}",
                MissionTriggerKind.Interval => $"{MissionText.Interval(intervalMinutes, intervalSeconds)}检查：{snippet}",
                _ => $"手动任务：{snippet}",
            };

        return new MissionDraft
        {
            Title = title,
            Goal = text,
            Trigger = trigger,
            Action = action,
            IntervalMinutes = intervalMinutes,
            IntervalSeconds = intervalSeconds,
            Target = target,
            Reason = BuildReason(trackVerb, schedule, remind, target, intervalMinutes, intervalSeconds, trigger, action),
        };
    }

    // ---------- 内部 ----------

    /// <summary>
    /// 解析巡检周期（分钟 / 秒，V4.1）。秒级用 <c>Seconds</c> 返回且 <c>Minutes=0</c>：
    /// "每 30 秒"必须走秒，否则会被"最小 1 分钟"的下限吃成 1 分钟。
    /// </summary>
    private static (int Minutes, int Seconds) ResolveInterval(string text, MissionTriggerKind trigger)
    {
        // 秒级优先：写了"每 N 秒"就不该再被后面的分钟规则覆盖
        var ms = EverySeconds.Match(text);
        if (ms.Success && int.TryParse(ms.Groups["n"].Value, out var sec) && sec > 0) return (0, sec);

        var msc = EverySecondsCn.Match(text);
        if (msc.Success)
        {
            var cnSec = ParseChineseNumber(msc.Groups["n"].Value);
            if (cnSec > 0) return (0, cnSec);
        }

        var m = EveryMinutes.Match(text);
        if (m.Success && int.TryParse(m.Groups["n"].Value, out var n) && n > 0) return (n, 0);

        var mc = EveryMinutesCn.Match(text);
        if (mc.Success)
        {
            var cn = ParseChineseNumber(mc.Groups["n"].Value);
            if (cn > 0) return (cn, 0);
        }

        if (text.Contains("每小时", StringComparison.Ordinal) || text.Contains("每个小时", StringComparison.Ordinal)) return (60, 0);
        if (text.Contains("每天", StringComparison.Ordinal) || text.Contains("每日", StringComparison.Ordinal)) return (1440, 0);
        if (text.Contains("每周", StringComparison.Ordinal) || text.Contains("每星期", StringComparison.Ordinal)) return (10080, 0);
        if (text.Contains("每月", StringComparison.Ordinal)) return (43200, 0);

        // 没有明确周期：追踪型默认 15 分钟巡检；其余默认 60 分钟。
        return (trigger == MissionTriggerKind.Watch ? 15 : 60, 0);
    }

    /// <summary>抽取"要盯/要回的对象"：自动回复有自己的说法（"自动回复X的消息"），其余沿用追踪动词。</summary>
    private static string ExtractTarget(
        string text, MissionTriggerKind trigger, MissionActionKind action, string? replyWord, string? trackVerb)
    {
        if (action == MissionActionKind.AutoReply)
        {
            // 先认"和/跟/与 X 智能聊天"（V4.1）：对象在动作词**之前**，且这是最不容易歧义的句式。
            // 顺序很重要——若先按"动作词之后"抽取，会把"，AI决定是否回复"里的词当成一个"人"。
            var afterAnd = TargetAfterAnd.Match(text);
            if (afterAnd.Success)
            {
                var t0 = SanitizeTarget(afterAnd.Groups["p"].Value);
                if (t0.Length > 0) return t0;
            }

            var t = ExtractTargetAfter(text, replyWord!);
            // "张晓明发消息就自动回复"：对象写在动作词之前
            if (t.Length == 0)
            {
                var i = text.IndexOf(replyWord!, StringComparison.Ordinal);
                var head = i > 0 ? text[..i] : "";
                var m = TargetBeforeReply.Match(head);
                if (m.Success) t = SanitizeTarget(m.Groups["p"].Value);
            }
            if (t.Length == 0 && trackVerb is not null) t = ExtractTargetAfter(text, trackVerb);
            return t;
        }

        return trigger == MissionTriggerKind.Watch && trackVerb is not null
            ? ExtractTargetAfter(text, trackVerb)
            : "";
    }

    /// <summary>从"……动词 X（的）消息"里抽出 X；抽不到返回空（不编造）。</summary>
    private static string ExtractTargetAfter(string text, string verb)
    {
        var i = text.IndexOf(verb, StringComparison.Ordinal);
        return i < 0 ? "" : SanitizeTarget(text[(i + verb.Length)..]);
    }

    private static string SanitizeTarget(string rest)
    {
        // 句尾语气词要一并去掉："帮我自动回复一下吧" → 不能把"吧"当成回复对象。
        rest = rest.TrimStart('一', '下', '着', '的', '帮', '我', '把', '给', '和', '与', '跟', '了',
                              '吧', '啊', '呀', '哦', '呢', '嘛', '哈', '：', ':', ' ', '「', '『');

        var cut = rest.Length;
        foreach (var s in TargetStops)
        {
            var j = rest.IndexOf(s, StringComparison.Ordinal);
            if (j > 0 && j < cut) cut = j;
        }

        var target = rest[..cut].Trim(' ', '的', '「', '」', '『', '』', '"', '“', '”', '《', '》', '（', '）', '(', ')');
        // 目标过长、含疑问、或只是"别人/对方"这类指代时放弃抽取，交给用户手动填。
        if (target.Length == 0 || target.Length > 24) return "";
        if (target.Contains('吗') || target.Contains('?') || target.Contains('？')) return "";
        if (PronounTargets.Contains(target, StringComparer.Ordinal)) return "";
        return target;
    }

    private static string Snippet(string text)
    {
        var t = text.Trim().TrimEnd('。', '，', '.', ',', '!', '！', '?', '？');
        return t.Length <= 14 ? t : t[..14] + "…";
    }

    private static string BuildReason(
        string? trackVerb, string? schedule, bool remind, string target,
        int intervalMinutes, int intervalSeconds, MissionTriggerKind trigger, MissionActionKind action)
    {
        var parts = new List<string>();
        if (action == MissionActionKind.AutoReply)
        {
            parts.Add("识别到「自动回复/智能聊天」意图");
            if (target.Length > 0) parts.Add($"回复对象「{target}」");
            parts.Add("只回复对方发来的新消息，能不能回、回什么都由模型决定，不会重复回、不会回自己");
            parts.Add("⚠ 创建后会自动发送（不再逐条确认）");
        }
        else
        {
            if (trackVerb is not null) parts.Add($"识别到「{trackVerb}」意图");
        }
        if (schedule is not null) parts.Add($"周期词「{schedule}」→ {MissionText.Interval(intervalMinutes, intervalSeconds)}");
        else if (trigger == MissionTriggerKind.Watch) parts.Add($"未指定周期，按默认 {MissionText.Interval(intervalMinutes, intervalSeconds)}巡检");
        if (target.Length > 0 && action != MissionActionKind.AutoReply) parts.Add($"追踪对象「{target}」");
        if (remind && action != MissionActionKind.AutoReply) parts.Add("有新内容时主动汇报（需你确认后才会创建）");
        parts.Add("草案未创建，等你确认");
        return string.Join("；", parts);
    }

    private static bool ContainsAny(string text, string[] words)
        => words.Any(w => text.Contains(w, StringComparison.Ordinal));

    /// <summary>疑问句特征（判断"用户在提问"而非"在派活"）。</summary>
    private static bool IsInterrogative(string text)
    {
        var t = text.TrimEnd();
        if (t.EndsWith('吗') || t.EndsWith('?') || t.EndsWith('？')) return true;
        return ContainsAny(text,
            ["是不是", "有没有", "什么时候", "几点", "多少", "多久", "哪些", "哪一天", "几次"]);
    }

    /// <summary>祈使语气（判断"用户在让我做事"，而不是在问自己的情况）。</summary>
    private static bool HasImperative(string text)
        => ContainsAny(text, ["帮我", "请", "给我", "替我", "要我", "麻烦", "看看", "查一下", "检查", "盯着"]);

    private static int ParseChineseNumber(string s)
    {
        if (s.Length == 0) return 0;
        if (s.Contains('十'))
        {
            var idx = s.IndexOf('十');
            var high = idx == 0 ? 1 : ParseDigit(s[..idx]);
            var low = idx == s.Length - 1 ? 0 : ParseDigit(s[(idx + 1)..]);
            return high * 10 + low;
        }
        return ParseDigit(s);
    }

    private static int ParseDigit(string s) => s switch
    {
        "一" => 1, "二" => 2, "两" => 2, "三" => 3, "四" => 4,
        "五" => 5, "六" => 6, "七" => 7, "八" => 8, "九" => 9,
        _ => 0,
    };
}
