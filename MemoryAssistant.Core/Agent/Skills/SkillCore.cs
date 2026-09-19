using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Planner;
using MemoryAssistant.Core.Missions;
using MemoryAssistant.Core.RAG;

namespace MemoryAssistant.Core.Agent.Skills;

/// <summary>Skill 的一次调用请求。</summary>
public sealed record SkillRequest
{
    public string Query { get; init; } = "";
    /// <summary>可选意图提示（Planner 产出或 IntentRouter hint）。</summary>
    public PlannerHint? Hint { get; init; }
    /// <summary>V3.3：检索时间窗（由 QueryModel 解析），Skill 应据此过滤候选/精读范围。</summary>
    public SearchWindow? Window { get; init; }

    /// <summary>
    /// 用户**原话**（未被规划器重述）。追问里"继续找他的最新记录"这种句子，
    /// 真正的对象在原话与会话上文里；规划器重述过的 goal 可能把它丢掉。
    /// </summary>
    public string? OriginalQuery { get; init; }
    /// <summary>会话上文：上一轮谈到的人（"他/那个人"指的就是这个人）。</summary>
    public string? LastPerson { get; init; }
    /// <summary>会话上文：上一轮的焦点短语。</summary>
    public string? Focus { get; init; }
    /// <summary>最近几轮问答（越靠后越近）。</summary>
    public IReadOnlyList<Answer.AnswerTurn> History { get; init; } = [];
    /// <summary>模型思考的增量回调（流式展示用；为空则不推）。</summary>
    public Action<string>? OnThinking { get; init; }
    /// <summary>
    /// 工具调用回调（每次调用推 start/finish 两条）。Skill 内部若直接执行了"动作"
    /// （如 ActionSkill 的打开会话/发送消息），也应推这里，让用户看到真实执行过程与结果。
    /// </summary>
    public Action<AgentProgress>? OnTool { get; init; }

    /// <summary>
    /// 本轮**必须真的调用**的工具名（第三阶段补充）：用户明确要求"发消息/回复"这类外部动作时由上层填入，
    /// 通用工具 Skill 会用它把 tool_choice 钉到该函数上。为空表示无此要求。
    /// </summary>
    public string? RequiredTool { get; init; }
}

/// <summary>
/// Skill 执行结果（plan2 §5 / §16 证据可溯源）：
/// Summary 给 Trace/阶段展示；Evidence 供回答引用；Draft 用于可直接作答的场景。
/// Sufficient 是"本步证据是否足够"的粗略信号，供 P15 Evaluator 消费。
/// </summary>
public sealed record SkillResult
{
    public string Skill { get; init; } = "";
    public bool Success { get; init; } = true;
    public bool Sufficient { get; init; }
    public string Summary { get; init; } = "";
    public string? Draft { get; init; }
    public IReadOnlyList<Evidence> Evidence { get; init; } = [];
    public string? Error { get; init; }
    public double ElapsedMs { get; init; }
    /// <summary>
    /// 本步模型的思考过程（多轮拼接：reasoning_content + 工具轮的意图自述）。
    /// 仅用于 Trace/UI 展示模型"为什么这么做"，不回填模型。
    /// </summary>
    public string? Reasoning { get; init; }
    /// <summary>本步 Agent 主循环实际发出的工具调用（含参数/结果摘要），供 Trace/UI 展示。</summary>
    public IReadOnlyList<ToolCallTrace> ToolCalls { get; init; } = [];
    /// <summary>
    /// 任务草案（对话式编排）：Skill 理解出的"可执行工作流"配置，交 UI 让用户确认后再落库。
    /// 非空表示本轮是"建任务"请求，UI 应弹确认卡而不是当普通回答。
    /// </summary>
    public MissionDraft? PendingMission { get; init; }
}

/// <summary>
/// Skill 的"能力底座"抽象（plan2 §5：Agent 决定做什么，代码负责怎么安全做）。
/// 底层可由 Infrastructure 用 Bridge/RAG/索引实现；单测用 FakeBackend。
/// </summary>
public interface IMemoryBackend
{
    /// <summary>语义召回候选片段（RAG）。</summary>
    Task<IReadOnlyList<RetrievedChunk>> SemanticSearchAsync(string query, int topK, CancellationToken ct);

    /// <summary>读取某会话某天（yyyy-MM-dd）的原文，转证据。</summary>
    Task<IReadOnlyList<Evidence>> ReadDayMessagesAsync(string sessionId, string date, int limit, CancellationToken ct);

    /// <summary>关键词全文检索消息，转证据。</summary>
    Task<IReadOnlyList<Evidence>> KeywordSearchAsync(string keyword, string? sessionId, int limit, CancellationToken ct);

    /// <summary>会话统计摘要文本（最近活跃会话/消息量/活跃日，limit=单会话样本，sessionLimit=会话数）。</summary>
    Task<string> GetStatsTextAsync(int limit, int sessionLimit, CancellationToken ct);

    /// <summary>最近 count 天的活跃分布（日期降序）。</summary>
    Task<IReadOnlyList<DayActivity>> GetRecentDaysAsync(int count, CancellationToken ct);

    /// <summary>某天的时间线片段（按会话聚合的当日原文摘要）。</summary>
    Task<IReadOnlyList<Evidence>> GetDaySnippetsAsync(string date, CancellationToken ct);

    /// <summary>
    /// 按人/会话名解析会话（备注名、昵称、群名都算），可选只要私聊或只要群。
    /// 默认实现返回空：不支持会话目录的底座（单测的 Fake）会自动退化为原来的检索路径。
    /// </summary>
    Task<IReadOnlyList<SessionHit>> FindSessionsAsync(string keyword, bool? privateOnly, int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SessionHit>>([]);

    /// <summary>
    /// 按会话 + 时间范围取"可直接展示"的消息（含解密后的图片/语音文件路径）。
    /// 默认实现返回空：底座不支持时 UI 退化为纯文字视图。
    /// </summary>
    Task<IReadOnlyList<RenderedMessage>> ReadRenderedAsync(string sessionId, long begin, long end, int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RenderedMessage>>([]);

    /// <summary>承诺候选（索引行级扫描）→ 证据。</summary>
    Task<IReadOnlyList<Evidence>> ScanCommitmentsAsync(int limit, CancellationToken ct);

    /// <summary>话题分析摘要文本（最近 recentDays 天）。</summary>
    Task<string> GetTopicsTextAsync(int recentDays, CancellationToken ct);

    /// <summary>会话画像摘要文本（前 top 个）。</summary>
    Task<string> GetProfilesTextAsync(int top, CancellationToken ct);
}

/// <summary>某日历日的活跃概况。</summary>
public sealed record DayActivity(string Date, int MessageCount);

/// <summary>按人/会话名解析出来的会话（用于"和某人的私聊/某个群"这类定位）。</summary>
public sealed record SessionHit(string Id, string DisplayName, bool IsGroup, long LastTimestamp)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
}

/// <summary>
/// 可直接展示的一条消息（仿微信会话视图用）：文字 / 图片 / 语音 / 表情包 / 文件…
/// 图片与语音是解密后的本地文件路径，UI 才能"原样显示"，而不是只看到 [图片] 占位。
/// </summary>
public sealed record RenderedMessage
{
    public long LocalId { get; init; }
    public long CreateTime { get; init; }
    public string SenderName { get; init; } = "";
    public bool IsSelf { get; init; }
    /// <summary>text | image | voice | video | sticker | app | system | other</summary>
    public string Kind { get; init; } = "text";
    public string Text { get; init; } = "";
    public string MediaPath { get; init; } = "";
    /// <summary>媒体解密失败原因（有值时 UI 如实说明，不装作没有）。</summary>
    public string MediaError { get; init; } = "";
}

/// <summary>一个可执行 Skill（与 SkillCatalog 名称一一对应）。</summary>
public interface IAgentSkill
{
    string Name { get; }
    string Description { get; }
    Task<SkillResult> ExecuteAsync(SkillRequest request, CancellationToken ct);
}
