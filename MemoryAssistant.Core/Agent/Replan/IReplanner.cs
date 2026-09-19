using MemoryAssistant.Core.Agent.Planner;

namespace MemoryAssistant.Core.Agent.Replan;

/// <summary>
/// 重规划器抽象（plan2 §7.3）：证据不足时给出"换一个策略"的新计划。
/// 必须避免重复已尝试的 Skill（attemptedSkills 由 Orchestrator 维护）。
/// </summary>
public interface IReplanner
{
    /// <summary>返回一个尚未尝试过的新单步计划；无可用策略返回 null（诚实终止）。</summary>
    AgentPlan? NextPlan(
        string goal,
        PlannerHint? hint,
        IReadOnlyCollection<string> attemptedSkills);
}
