using MemoryAssistant.Core.Agent.Planner;
using MemoryAssistant.Core.Agent.Query;

namespace MemoryAssistant.Core.Agent.Conversation;

/// <summary>一轮问答记录（会话层，不重复执行）。</summary>
public sealed record ChatTurn
{
    public string OriginalQuery { get; init; } = "";
    /// <summary>解析/改写后的实际执行查询（如"他" → 具体人名）。</summary>
    public string ResolvedQuery { get; init; } = "";
    public string Answer { get; init; } = "";
    /// <summary>本轮最终引用证据（保持 [N] 编号，供"展开第 N 条"）。</summary>
    public IReadOnlyList<Evidence> Evidence { get; init; } = [];
    public string? Note { get; init; }
    public PlannerHint? Hint { get; init; }
    /// <summary>本轮结构化目标模型（P18：实体/时间/约束/证据要求）。</summary>
    public QueryModel? Model { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 会话记忆（plan2 §9）：聊天历史 + 最近证据 + 指代上下文（人/焦点）。
/// 让"他/那次/还有吗/把第三条展开"不必从零重跑：解析器基于这里记录的实体续写下一轮目标。
/// </summary>
public sealed class ConversationSession
{
    private readonly List<ChatTurn> _turns = [];

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public IReadOnlyList<ChatTurn> Turns => _turns;

    /// <summary>上一轮涉及的人/实体（"他"的指代对象）。</summary>
    public string? LastPerson { get; private set; }
    /// <summary>简短焦点短语（供"还有吗/再早一点"续写）。</summary>
    public string? Focus { get; private set; }
    public ChatTurn? Last => _turns.Count > 0 ? _turns[^1] : null;

    public void RecordTurn(
        string originalQuery,
        string resolvedQuery,
        string answer,
        IReadOnlyList<Evidence> evidence,
        string? note = null,
        PlannerHint? hint = null,
        QueryModel? model = null)
    {
        _turns.Add(new ChatTurn
        {
            OriginalQuery = originalQuery,
            ResolvedQuery = resolvedQuery,
            Answer = answer,
            Evidence = evidence,
            Note = note,
            Hint = hint,
            Model = model,
        });

        // 更新指代上下文：本轮若明确提到"和X聊/找X/X"类实体则替换 LastPerson。
        // 写操作的目标也算"本轮谈到的人"：用户说过"给张晓明发消息：A"，下一句"再发送一句B"
        // 里的对象就是她——而 ExtractPerson 只认"和/跟/与/找/问"，对"给X发消息"是瞎的，
        // 不补这一条，追问就会被判成"没说是发给谁"（实测踩过）。
        var person = ExtractPerson(originalQuery)
                     ?? Action.ActionIntentParser.TryParse(originalQuery)?.Target;
        if (!string.IsNullOrEmpty(person))
            LastPerson = person;

        Focus = CompactFocus(person ?? Focus ?? originalQuery);
    }

    /// <summary>按引用编号取上一轮证据（"展开第 N 条"用）。</summary>
    public Evidence? LastEvidenceByIndex(int n)
        => Last?.Evidence.FirstOrDefault(e => e.Index == n);

    /// <summary>代词/占位词：它们描述的是"指代关系"而不是某个人，绝不能当成实体。</summary>
    private static readonly HashSet<string> Pronouns = new(StringComparer.Ordinal)
    {
        "他", "她", "它", "他们", "她们", "你", "你们", "我", "我们", "咱", "咱们", "您",
        "那个人", "那人", "这人", "这个人", "此人", "该人", "对方", "本人", "自己",
        "谁", "哪个", "什么", "人", "别人", "朋友", "大家",
    };

    /// <summary>从提问中抓"和/跟/与/找 X 聊/说/…"人物实体：X 惰性匹配，后必须跟话语标记/时间词首字/标点/结尾，避免吞进整句。</summary>
    public static string? ExtractPerson(string query)
    {
        var m = System.Text.RegularExpressions.Regex.Match(query,
            @"(?:和|跟|与|找|问)([\u4e00-\u9fa5A-Za-z0-9_@\-]{1,12}?)(?=(?:聊|说|讲|谈|问|的|上|去|今|最|前|这|下|，|,|。|．|\.|$))");
        var name = m.Groups[1].Value.Trim();
        // 名字后面常跟着日期（"我和张晓明9月11号聊了什么"）：把日期尾巴切掉，否则实体变成"张晓明9月11号"，
        // 后面按人过滤/匹配全会落空。
        name = System.Text.RegularExpressions.Regex.Replace(name,
            @"(?:\d{1,4}\s*年)?\s*\d{1,2}\s*月\s*\d{1,2}\s*[日号]?$|\d{1,2}\s*[日号]$|\d{1,2}\s*[/\-．.]\s*\d{1,2}$", "").Trim();
        if (!IsUsablePerson(name)) return null;
        // 正则常把动词一起吃进来（"找那个人"）：剥掉前缀动词后如果是个代词，说明这压根不是人名
        if (!IsUsablePerson(name.TrimStart(VerbChars))) return null;
        return name;
    }

    /// <summary>名字里出现这些字基本就不是人名（是"指代"）："找他""那个人""你朋友"…</summary>
    private static readonly char[] PronounChars = ['他', '她', '它', '你', '我', '咱', '您', '谁'];

    /// <summary>人名前面可能粘上动词（"找那个人"里"找"是动词不是名字的一部分）。</summary>
    private static readonly char[] VerbChars = ['找', '问', '和', '跟', '与'];

    /// <summary>
    /// 这个名字能不能当"实体"用。代词必须排除——注意"那个人"里**不含"他"字**（那+个+人），
    /// 光靠"名字里有没有代词字"抓不到它，必须比对整词；而且正则常把动词一起抓进来（"找那个人"），
    /// 所以剥掉前缀动词后再比一次。不这么做，LastPerson 会被写成"那个人"，
    /// 下一轮"他/那个人"就再也指不到人了（实测就是这么把上下文弄丢的）。
    /// </summary>
    private static bool IsUsablePerson(string name)
    {
        if (string.IsNullOrEmpty(name) || Pronouns.Contains(name)) return false;
        // 用 IndexOfAny 而不是 LINQ 的 Any/Contains：语义直白、不受扩展方法解析影响
        return name.IndexOfAny(PronounChars) < 0;
    }

    private static string CompactFocus(string text)
    {
        var t = text.Trim();
        return t.Length <= 14 ? t : t[..14] + "…";
    }
}
