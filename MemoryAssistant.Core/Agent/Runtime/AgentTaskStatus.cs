namespace MemoryAssistant.Core.Agent.Runtime;

/// <summary>Agent 任务生命周期状态（plan2 §3.1）。可被 UI/日志观察。</summary>
public enum AgentTaskStatus
{
    Created,       // 任务已创建，尚未启动
    Planning,      // 正在规划目标/策略
    Executing,     // 正在执行（调用 Skill / Tool）
    Evaluating,    // 正在评估结果是否足够
    Replanning,    // 结果不足，正在更换策略
    Completed,     // 正常完成
    Failed,        // 失败（含超时）
    Cancelled,     // 用户取消
}

/// <summary>任务内步骤状态（plan2 §3：步骤与聊天历史分离）。</summary>
public enum TaskStepStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped,
}
