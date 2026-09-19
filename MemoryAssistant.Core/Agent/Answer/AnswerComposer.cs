namespace MemoryAssistant.Core.Agent.Answer;

/// <summary>一问一答的一轮上文（压缩后喂给作答器，避免"每轮都从零开始"）。</summary>
public sealed record AnswerTurn(string UserQuery, string Answer);

/// <summary>
/// 作答输入：把"任务跑完后的产物"整理成一份可交给模型组织语言的材料包。
/// 关键约束：<see cref="Evidence"/> 里的每条都带稳定编号，答案只能引用这些编号，杜绝编造。
/// </summary>
public sealed record AnswerRequest
{
    public string UserQuery { get; init; } = "";
    /// <summary>本轮最终目标（可能被追问改写，如"展开第 1 条"）。</summary>
    public string Goal { get; init; } = "";
    /// <summary>可引用的原始材料（原文优先，rag 片段次之）。</summary>
    public IReadOnlyList<Evidence> Evidence { get; init; } = [];
    /// <summary>分析类结论（统计/话题/画像/承诺扫描等），属推断而非原文。</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>最近的对话回合（越近越靠后）：让回答接得上"他/那条/刚才说的"，像真人一样有连续性。</summary>
    public IReadOnlyList<AnswerTurn> History { get; init; } = [];
    /// <summary>上一轮涉及的人/焦点（会话记忆），用于自然衔接。</summary>
    public string? LastPerson { get; init; }
    public string? Focus { get; init; }
    /// <summary>任务是否正常收尾（false 时答案需如实说明未完成原因）。</summary>
    public bool Completed { get; init; } = true;
    public string? StopReason { get; init; }
    /// <summary>
    /// 无需查记录的直接回答（打招呼/闲聊/能力咨询）：此时材料为空也要让模型开口，
    /// 否则用户会收到一句写死的模板话（体验最差的一类回复）。
    /// </summary>
    public bool DirectReply { get; init; }
}

/// <summary>
/// 作答器：把材料组织成"像人说的"回答（plan2 §24 的最后一公里）。
/// 实现可为 LLM（自然语言），也可为确定性兜底；返回 null 表示放弃，由上层的确定性文案接管。
/// </summary>
public interface IAnswerComposer
{
    /// <summary>
    /// 组织成回答。返回 null 表示放弃，由上层确定性文案接管。
    /// <paramref name="onDelta"/> 非空且实现支持流式时，正文会边生成边回调（UI 不必等整段生成完）。
    /// </summary>
    Task<string?> ComposeAsync(AnswerRequest request, CancellationToken ct, Action<string>? onDelta = null);
}
