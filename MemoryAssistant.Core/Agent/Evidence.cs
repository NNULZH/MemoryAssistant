namespace MemoryAssistant.Core.Agent;

/// <summary>证据生命周期（plan2 §8.2）：候选 → 已观测 → 已验证 → 已引用。</summary>
public enum EvidenceStage
{
    Candidate,   // RAG/规则初筛命中，尚未精读确认
    Observed,    // 已读到（工具/Skill 产出）
    Verified,    // 已核对为可信（原文/交叉确认）
    Cited,       // 已写入最终回答引用 [N]
}

/// <summary>事实与推测分离（plan2 §8.3）：避免把统计相关性写成事实。</summary>
public enum EvidenceKind
{
    Fact,       // 原文/可直接核实的客观事实
    Inference,  // 统计/聚类/模型推断（需要说明依据）
    Unknown,
}

/// <summary>
/// 一条证据：来自某个会话/时间点的原文片段（或统计推断）。
/// 生命周期字段由 EvidenceStore 管理（record 不可变，Store 负责 replace）。
/// </summary>
public sealed record Evidence
{
    /// <summary>稳定编号（EvidenceStore 内 1..N，作为回答引用 [N]）。</summary>
    public int Index { get; init; }
    public string SessionId { get; init; } = "";
    public string SessionDisplayName { get; init; } = "";
    /// <summary>
    /// 记录所属日期（yyyy-MM-dd，来自索引片段）。
    /// 必须显式带出来：否则模型只能看到文本里的"[09:17]"，会把用户问的日期当成事实复述
    /// （例如把 9.8 的记录说成"9 月 11 号你们聊了…"）。
    /// </summary>
    public string Date { get; init; } = "";
    public long CreateTime { get; init; }
    public string SenderName { get; init; } = "";
    public string Content { get; init; } = "";
    /// <summary>来源类型：chat | rag | stats | 承诺 | 时间线 等。</summary>
    public string Source { get; init; } = "chat";
    /// <summary>由哪个 Skill/Tool 发现（plan2 §8.1 origin tool）。</summary>
    public string? OriginTool { get; init; }
    public double Confidence { get; init; } = 0.8;
    public EvidenceStage Stage { get; init; } = EvidenceStage.Observed;
    public EvidenceKind Kind { get; init; } = EvidenceKind.Fact;
    public bool Verified => Stage is EvidenceStage.Verified or EvidenceStage.Cited;

    public string ToCitation() => $"[{Index}]";
}
