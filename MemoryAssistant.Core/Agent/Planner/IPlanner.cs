namespace MemoryAssistant.Core.Agent.Planner;

/// <summary>
/// 规划器抽象（plan2 §4）：输入用户查询 + 可选 Hint，输出已验证的结构化计划。
/// 实现负责：LLM 规划 → 校验 → 失败回退规则计划（保证始终返回可用计划）。
/// </summary>
public interface IPlanner
{
    /// <summary>目录中可选的 Skill（给上层展示/给 LLM 提示）。</summary>
    SkillCatalog Catalog { get; }

    /// <summary>
    /// 生成计划。preferLlm=false 时直接走规则（省 token）；
    /// 默认 true：LLM 失败/输出非法 → 规则回退，绝不让调用方拿到坏计划。
    /// </summary>
    /// <param name="onReasoningDelta">规划时的思考增量（非空且客户端支持流式时边生成边推，供 UI 展示）。</param>
    Task<AgentPlan> PlanAsync(
        string query,
        PlannerHint? hint = null,
        bool preferLlm = true,
        CancellationToken ct = default,
        Action<string>? onReasoningDelta = null);
}
