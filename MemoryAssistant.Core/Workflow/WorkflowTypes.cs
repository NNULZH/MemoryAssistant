namespace MemoryAssistant.Core.Workflow;

public enum IntentKind
{
    Recall,       // 回忆聊天内容
    Stats,        // 统计/排行
    Commitment,   // 承诺/待办（P7）
    Chitchat,     // 普通闲聊，不动工具不碰 RAG
    Unknown
}

/// <summary>意图路由结果。</summary>
public sealed record IntentResult
{
    public IntentKind Intent { get; init; } = IntentKind.Unknown;
    /// <summary>涉及的实体（人名/群名等）。</summary>
    public string Entity { get; init; } = "";
    /// <summary>时间线索（如"去年这个时候""最近"）。</summary>
    public string TimeHint { get; init; } = "";
    public string Raw { get; init; } = "";
    public bool FromRule { get; init; }
}

/// <summary>Workflow 阶段事件（与 StageTimings 同 key，供 Trace 展示）。</summary>
public sealed record WorkflowStageEvent(string Stage, string Detail);

/// <summary>Workflow 执行结果：最终答案 + 意图 + 证据 + Agent 轨迹。</summary>
public sealed record WorkflowResult
{
    public string Answer { get; init; } = "";
    public IntentResult Intent { get; init; } = new();
    public IReadOnlyList<Core.Agent.Evidence> Evidence { get; init; } = [];
    public Core.Agent.AgentResult? AgentResult { get; init; }
    public double TotalElapsedMs { get; init; }
    /// <summary>各阶段耗时（答辩 Agent Trace 用）。</summary>
    public Dictionary<string, double> StageTimings { get; init; } = new();
    /// <summary>各阶段事件文本（Agent Trace 展示用）。</summary>
    public IReadOnlyList<WorkflowStageEvent> StageEvents { get; init; } = [];
}
