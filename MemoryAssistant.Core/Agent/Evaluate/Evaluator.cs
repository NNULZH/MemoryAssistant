using MemoryAssistant.Core.Agent.Planner;
using MemoryAssistant.Core.Agent.Skills;

namespace MemoryAssistant.Core.Agent.Evaluate;

/// <summary>
/// 评估结论（plan2 §7.1）：结构化判断"是否足以作答 / 下一步怎么办"。
/// 确定性默认实现；未来可换 LLM 评估器，接口保持不变。
/// </summary>
public sealed record Evaluation
{
    public bool Sufficient { get; init; }
    /// <summary>0..1 的粗略置信度（供 Trace/UI，不做人格推断）。</summary>
    public double Confidence { get; init; }
    public IReadOnlyList<string> Missing { get; init; } = [];
    /// <summary>answer | replan | stop。</summary>
    public string RecommendedAction { get; init; } = "stop";
    public string Reason { get; init; } = "";
}

/// <summary>评估输入：本轮计划执行结果 + 剩余轮次等控制信息。</summary>
public sealed record EvaluationInput
{
    public required AgentPlan Plan { get; init; }
    public required PlanExecutionResult Run { get; init; }
    public int ReplanRoundsUsed { get; init; }
    public int MaxReplanRounds { get; init; } = 3;
}

public interface IEvaluator
{
    Evaluation Evaluate(EvaluationInput input);
}
