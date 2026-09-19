using MemoryAssistant.Core.Agent.Planner;

namespace MemoryAssistant.Core.Agent.Query;

/// <summary>时间线索粒度（供检索/精读缩小范围）。</summary>
public enum TimeKind
{
    None,
    SpecificDate, // 具体日期/日期段
    Today,
    ThisWeek,
    LastWeek,
    ThisMonth,
    LastMonth,
    ThisYear,
    LastYear,
    RangeDays,    // 最近 N 天/周/月/年
    Recent,       // 最近/近期（模糊）
}

/// <summary>
/// 结构化目标模型（plan2 §10 / P18）：把用户问题从"一个 intent 分类"提升为
/// { Goal / Entities / TimeRange / Constraints / EvidenceRequirement / IntentHint }。
/// 仍是低成本 Hint——真正决策由 Agent Runtime 做。
/// </summary>
public sealed record QueryModel
{
    public string Raw { get; init; } = "";
    /// <summary>一句话目标（默认为原问题）。</summary>
    public string Goal { get; init; } = "";
    /// <summary>低成本意图提示：recall|stats|commitment|topic|profile|chitchat。</summary>
    public string IntentHint { get; init; } = "recall";
    /// <summary>实体（人名/会话），从"和/跟/找 X 聊"抽取。</summary>
    public IReadOnlyList<string> Entities { get; init; } = [];
    public TimeKind TimeKind { get; init; }
    public string TimePhrase { get; init; } = "";
    /// <summary>检索关键词提示。</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];
    /// <summary>约束（如"仅查聊天记录"）。</summary>
    public IReadOnlyList<string> Constraints { get; init; } = [];
    /// <summary>是否要求真实原文证据（提到/有没有/说过/原话…）。</summary>
    public bool NeedsOriginalEvidence { get; init; }

    public PlannerHint ToHint()
        => new(IntentHint, Entities.FirstOrDefault(), TimePhrase);
}

public interface IQueryAnalyzer
{
    QueryModel Analyze(string raw);
}

/// <summary>
/// 规则版目标抽取（确定性、零 token）。正则表尽量克制，避免过度工程；
/// 未来可换 LLM 版，接口不变。
/// </summary>
public sealed class RuleQueryAnalyzer : IQueryAnalyzer
{
    private static readonly (string Kind, string[] Words)[] IntentRules =
    [
        ("commitment", ["承诺", "答应", "待办", "还没做", "没做", "欠", "答应过", "flag"]),
        ("stats", ["经常", "频繁", "排行", "几次", "多少次", "多久", "多少", "统计", "半夜", "深夜", "凌晨", "几点", "时段", "活跃", "消息量"]),
        ("topic", ["话题", "主题", "在聊", "聊什么", "热点", "都在聊"]),
        ("profile", ["画像", "是个什么样", "什么样的人", "什么性格"]),
        ("chitchat", ["你好", "您好", "谢谢", "再见", "你是谁", "在吗", "哈哈"]),
    ];

    private static readonly (TimeKind Kind, string[] Words)[] TimeRules =
    [
        (TimeKind.LastYear, ["去年", "前年"]),
        (TimeKind.ThisYear, ["今年", "这年"]),
        (TimeKind.LastWeek, ["上周", "上星期"]),
        (TimeKind.ThisWeek, ["这周", "本周"]),
        (TimeKind.LastMonth, ["上个月", "上月"]),
        (TimeKind.ThisMonth, ["这个月", "本月", "这个月"]),
        (TimeKind.Today, ["今天", "昨天", "前天"]),
        (TimeKind.Recent, ["最近", "近期", "最近这段", "这段时间", "这几天"]),
    ];

    private static readonly string[] EvidenceMarkers = ["提到", "有没有", "有没有提", "说过", "原话", "怎么说的", "有没有聊", "记录里"];

    public QueryModel Analyze(string raw)
    {
        var text = (raw ?? "").Trim();
        var lower = text.ToLowerInvariant();

        // 意图
        string intent = "recall";
        foreach (var (kind, words) in IntentRules)
        {
            if (words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)))
            {
                intent = kind;
                break;
            }
        }
        if (intent == "recall" && (text.Contains("聊天", StringComparison.Ordinal)
            || text.Contains("记录", StringComparison.Ordinal))) { /* 保持 recall */ }

        // 实体：和/跟/找 X 聊…
        var entities = new List<string>();
        var person = Conversation.ConversationSession.ExtractPerson(text);
        if (!string.IsNullOrEmpty(person)) entities.Add(person);

        // 时间（先具体日期，再"最近 N 天"区间，再关键词档）
        var timeKind = TimeKind.None;
        string timePhrase = "";
        if (DatePhrase.TryFind(text, out _, out var dateText))
        {
            timeKind = TimeKind.SpecificDate;
            timePhrase = dateText;
        }
        else
        {
            var range = System.Text.RegularExpressions.Regex.Match(text, @"近?\s*([1-9]\d{0,2})\s*(天|周|个月|星期|年)");
            if (range.Success)
            {
                timeKind = TimeKind.RangeDays;
                timePhrase = range.Value;
            }
            else
            {
                foreach (var (kind, words) in TimeRules)
                {
                    var hit = words.FirstOrDefault(w => text.Contains(w, StringComparison.Ordinal));
                    if (hit is not null)
                    {
                        timeKind = kind;
                        timePhrase = hit;
                        break;
                    }
                }
            }
        }

        // 关键词（实体 + 时间 + 引号内短语）
        var keywords = new List<string>();
        keywords.AddRange(entities);
        if (!string.IsNullOrEmpty(timePhrase)) keywords.Add(timePhrase);
        foreach (System.Text.RegularExpressions.Match q in System.Text.RegularExpressions.Regex.Matches(text, "“([^”]+)”|「([^」]+)」"))
        {
            var k = (q.Groups[1].Success ? q.Groups[1].Value : q.Groups[2].Value).Trim();
            if (k.Length > 0) keywords.Add(k);
        }

        // 约束 & 证据要求
        var constraints = new List<string>();
        if (text.Contains("记录", StringComparison.Ordinal) || text.Contains("聊天记录", StringComparison.Ordinal))
            constraints.Add("仅查聊天记录");
        if (text.Contains("只查", StringComparison.Ordinal) || text.Contains("只要", StringComparison.Ordinal))
            constraints.Add("限定范围");
        bool needsOriginal = intent is "recall" or "commitment"
            && EvidenceMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));

        return new QueryModel
        {
            Raw = text,
            Goal = text.Length > 0 ? text : "（空查询）",
            IntentHint = intent,
            Entities = entities,
            TimeKind = timeKind,
            TimePhrase = timePhrase,
            Keywords = keywords,
            Constraints = constraints,
            NeedsOriginalEvidence = needsOriginal,
        };
    }
}
