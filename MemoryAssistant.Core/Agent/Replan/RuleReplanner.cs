using MemoryAssistant.Core.Agent.Planner;

namespace MemoryAssistant.Core.Agent.Replan;

/// <summary>
/// 确定性重规划器：按"初始主策略"给出换策略梯子（plan2 验收场景 C：
/// 第一策略失败 → 改语义检索/联系人+时间范围 等替代策略）。
/// 梯子顺序按能力互补性组织，绝不重复 attemptedSkills。
/// </summary>
public sealed class RuleReplanner : IReplanner
{
    private static readonly Dictionary<string, string[]> Ladders = new(StringComparer.Ordinal)
    {
        ["recall"] = ["timeline", "stats", "profile", "topic", "commitment"],
        ["stats"] = ["timeline", "topic", "recall", "profile"],
        ["timeline"] = ["recall", "stats", "topic"],
        ["commitment"] = ["recall", "profile", "timeline"],
        ["topic"] = ["recall", "timeline", "stats"],
        ["profile"] = ["stats", "recall", "timeline"],
    };

    public AgentPlan? NextPlan(string goal, PlannerHint? hint, IReadOnlyCollection<string> attemptedSkills)
    {
        var initial = RulePlanner.RulePlan(goal, hint);
        var primary = initial.Steps.FirstOrDefault(s => s.Kind == PlanStepKind.Skill)?.Name ?? "recall";
        if (!Ladders.TryGetValue(primary, out var ladder))
            ladder = ["recall", "timeline", "stats", "topic", "profile"];

        foreach (var skill in ladder)
        {
            if (attemptedSkills.Contains(skill, StringComparer.Ordinal)) continue;
            return new AgentPlan
            {
                Goal = goal,
                FromRules = true,
                Steps =
                [
                    new PlanStep { Id = "r1", Kind = PlanStepKind.Skill, Name = skill, Reason = $"换策略：{skill}" },
                ],
                StopCondition = "证据足够即可回答",
            };
        }

        // 兜底：内建记忆能力都试过仍不够时，交给"调用工具"（V3.5b）。
        // 需要 LLM；验收节流模式(Eco)/未配置模型时该 Skill 会诚实降级，不会硬撑。
        if (!attemptedSkills.Contains("tools", StringComparer.Ordinal)
            && !string.Equals(primary, "tools", StringComparison.Ordinal)
            && !attemptedSkills.Contains("action", StringComparer.Ordinal))
        {
            return new AgentPlan
            {
                Goal = goal,
                FromRules = true,
                Steps =
                [
                    new PlanStep { Id = "r1", Kind = PlanStepKind.Skill, Name = "tools", Reason = "换策略：调用工具" },
                ],
                StopCondition = "证据足够即可回答",
            };
        }

        return null; // 所有互补策略都试过 → 交由上层诚实终止
    }
}
