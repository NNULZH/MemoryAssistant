using MemoryAssistant.Core.Configuration;

namespace MemoryAssistant.Core.Agent.Planner;

/// <summary>
/// 代码级计划校验与修复（plan2 §4.3）：
///  - step id 非空且不重复
///  - 步骤数 1..MaxPlanSteps
///  - kind ∈ {skill, verify, finish}
///  - kind=skill 时 Skill 必须存在于目录
///  - 禁止未知操作 / 未授权动作（当前只开放只读能力）
/// 校验失败由上层走 Repair 或回退规则计划。
/// </summary>
public sealed class PlanValidator
{
    private readonly SkillCatalog _catalog;
    private readonly int _maxSteps;

    public PlanValidator(SkillCatalog catalog, AgentOptions options)
    {
        _catalog = catalog;
        _maxSteps = options.MaxPlanSteps > 0 ? options.MaxPlanSteps : 8;
    }

    public PlanValidation Validate(AgentPlan? plan)
    {
        if (plan is null)
            return new PlanValidation { Ok = false, Errors = ["计划为空"] };

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(plan.Goal))
            errors.Add("缺少 goal");

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var clean = new List<PlanStep>();
        foreach (var step in plan.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Id))
            {
                errors.Add("存在缺少 id 的步骤");
                continue; // 可修复：丢弃
            }
            if (!seenIds.Add(step.Id))
            {
                errors.Add($"步骤 id 重复：{step.Id}");
                continue; // 可修复：保留首个
            }
            if (!PlanStepKind.IsAllowed(step.Kind))
            {
                errors.Add($"未知步骤类型：{step.Kind}（允许 {string.Join('/', PlanStepKind.Allowed)}）");
                continue; // 可修复：丢弃
            }
            if (step.Kind == PlanStepKind.Skill && !_catalog.Contains(step.Name))
            {
                errors.Add($"未知 Skill：{step.Name}");
                continue; // 可修复：丢弃
            }
            clean.Add(step);
        }

        if (plan.Steps.Count == 0)
            errors.Add("计划没有步骤");
        if (plan.Steps.Count > _maxSteps)
            errors.Add($"步骤数 {plan.Steps.Count} 超过上限 {_maxSteps}");

        var ok = errors.Count == 0;
        if (ok)
            return new PlanValidation { Ok = true, Repaired = plan };

        // Repair：只保留合法步骤；若空则不可修复。
        AgentPlan? repaired = null;
        if (clean.Count > 0 && clean.Count <= _maxSteps)
        {
            repaired = new AgentPlan
            {
                Goal = string.IsNullOrWhiteSpace(plan.Goal) ? "(目标缺失，由规则回退补齐)" : plan.Goal,
                Steps = clean,
                StopCondition = plan.StopCondition,
                Reasoning = plan.Reasoning,
            };
        }
        return new PlanValidation { Ok = false, Errors = errors, Repaired = repaired };
    }
}
