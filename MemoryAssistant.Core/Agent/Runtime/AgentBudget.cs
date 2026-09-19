using MemoryAssistant.Core.Configuration;

namespace MemoryAssistant.Core.Agent.Runtime;

/// <summary>
/// 任务级预算计数器（plan2 §6.3 / §16）：工具次数、失败次数。
/// Executor 在每次实际工具/步骤前调用 TryConsume，超限即停，避免无限烧 token。
/// </summary>
public sealed class AgentBudget
{
    public AgentBudget(AgentOptions options)
    {
        MaxToolCalls = options.MaxToolCalls > 0 ? options.MaxToolCalls : 20;
        MaxFailures = options.MaxFailures > 0 ? options.MaxFailures : 3;
    }

    public int MaxToolCalls { get; }
    public int MaxFailures { get; }
    public int ToolCallCount { get; private set; }
    public int FailureCount { get; private set; }

    public bool ToolsExhausted => ToolCallCount >= MaxToolCalls;
    public bool FailuresExhausted => FailureCount >= MaxFailures;

    /// <summary>尝试消耗一次工具调用额度；成功返回 true，超限返回 false（调用方应停止并更换策略）。</summary>
    public bool TryConsumeTool()
    {
        if (ToolsExhausted) return false;
        ToolCallCount++;
        return true;
    }

    public void RegisterFailure() => FailureCount++;
}
