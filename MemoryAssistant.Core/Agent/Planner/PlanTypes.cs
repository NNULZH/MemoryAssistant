namespace MemoryAssistant.Core.Agent.Planner;

/// <summary>允许的计划步骤类型（plan2 §4.2：type 字段）。</summary>
public static class PlanStepKind
{
    public const string Skill = "skill";   // 调用已注册 Skill（如 recall/stats）
    public const string Verify = "verify"; // 验证/精读确认
    public const string Finish = "finish"; // 直接回答/结束

    public static readonly string[] Allowed = [Skill, Verify, Finish];

    public static bool IsAllowed(string kind)
        => Allowed.Contains(kind, StringComparer.Ordinal);
}

/// <summary>计划中的一步（LLM/规则产出，执行前必须先过 PlanValidator）。</summary>
public sealed record PlanStep
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = PlanStepKind.Skill;
    public string Name { get; init; } = "";   // kind=skill 时为 skill 名
    public string? Reason { get; init; }
}

/// <summary>
/// 结构化执行计划（plan2 §4.2）。LLM 必须以 JSON 形式产出，不能只有自由文本。
/// </summary>
public sealed record AgentPlan
{
    public string Goal { get; init; } = "";
    public IReadOnlyList<PlanStep> Steps { get; init; } = [];
    public string? StopCondition { get; init; }
    public bool FromLlm { get; init; }
    public bool FromRules { get; init; }
    /// <summary>
    /// LLM 规划时的思考过程（reasoning_content）。规则计划为空。
    /// 展示"模型是怎么理解意图并决定做什么"的第一现场。
    /// </summary>
    public string? Reasoning { get; init; }
}

/// <summary>规划提示（IntentRouter 输出降级为 Hint，Agent 可忽略/重新解释，plan2 §1.3）。</summary>
public sealed record PlannerHint(string? Intent, string? Entity, string? TimeHint)
{
    public bool HasValue =>
        !string.IsNullOrWhiteSpace(Intent)
        || !string.IsNullOrWhiteSpace(Entity)
        || !string.IsNullOrWhiteSpace(TimeHint);
}

/// <summary>计划校验结果（plan2 §4.3：PASS→执行；FAIL→Repair/Fallback）。</summary>
public sealed record PlanValidation
{
    public bool Ok { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    /// <summary>Repair 后的安全计划（丢弃非法步骤；为空时仍 Ok=false）。</summary>
    public AgentPlan? Repaired { get; init; }
}
