namespace MemoryAssistant.Core.Agent.Runtime;

/// <summary>任务计划中的一步（plan2 §4：结构化计划单元；P12 Planner 生成）。</summary>
public sealed class AgentTaskStep
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>步骤类型：skill | tool | verify | finish。</summary>
    public string Kind { get; init; } = "skill";
    /// <summary>目标能力名称（Skill/Tool 名，如 stats / timeline）。</summary>
    public string Name { get; init; } = "";
    public string? Reason { get; init; }
    public TaskStepStatus Status { get; set; } = TaskStepStatus.Pending;

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>执行结论摘要（例如“找到 3 个候选会话”）。</summary>
    public string? Summary { get; set; }
    public string? Error { get; set; }
    public string? ObservationRef { get; set; }

    public AgentTaskStep MarkRunning(DateTimeOffset? now = null)
    {
        Status = TaskStepStatus.Running;
        StartedAt = now ?? DateTimeOffset.UtcNow;
        return this;
    }

    public AgentTaskStep Complete(string? summary, DateTimeOffset? now = null)
    {
        Status = TaskStepStatus.Completed;
        Summary = summary;
        FinishedAt = now ?? DateTimeOffset.UtcNow;
        return this;
    }

    public AgentTaskStep Fail(string error, DateTimeOffset? now = null)
    {
        Status = TaskStepStatus.Failed;
        Error = error;
        FinishedAt = now ?? DateTimeOffset.UtcNow;
        return this;
    }
}
