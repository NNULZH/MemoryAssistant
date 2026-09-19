namespace MemoryAssistant.Core.Agent.Runtime;

/// <summary>
/// 任务执行器抽象：把“用户任务 → 最终 AgentResult”的一种策略。
/// P11 用 WorkflowExecutor 包装旧 WorkflowEngine；P12/P13 起替换为
/// Planner + Skill 编排实现，TaskRuntime 无需改动。
/// </summary>
public interface IAgentTaskExecutor
{
    string Name { get; }

    /// <summary>
    /// 执行任务。允许向 task.State 写入步骤/观测/证据，并通过 budget 声明工具消耗。
    /// 通过 ct 响应取消与超时。
    /// </summary>
    Task<AgentResult> ExecuteAsync(AgentTask task, AgentBudget budget, CancellationToken ct);
}
