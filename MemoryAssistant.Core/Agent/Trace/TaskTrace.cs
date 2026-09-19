namespace MemoryAssistant.Core.Agent.Trace;

/// <summary>
/// 单步执行轨迹（Skill 粒度）：摘要 + 本步模型思考过程 + 工具调用。
/// 说明：早期设计（plan2 §19）为"绝不携带模型思维链"，现按用户明确要求改为**展示推理模型的
/// 思考过程**——Reasoning 只进 Trace/UI（调试面板与 --think 验收），不参与任何后续 LLM 请求。
/// </summary>
public sealed record CycleStepTrace
{
    public string Skill { get; init; } = "";
    public bool Success { get; init; } = true;
    public string Summary { get; init; } = "";
    public string? Error { get; init; }
    public double LatencyMs { get; init; }
    /// <summary>本步模型的思考过程（DeepSeek reasoner 的 reasoning_content + 工具轮意图自述）。</summary>
    public string? Reasoning { get; init; }
    /// <summary>本步实际发出的工具调用（含参数/结果），供 UI/验收观察模型是否会自主调工具。</summary>
    public IReadOnlyList<Agent.ToolCallTrace> ToolCalls { get; init; } = [];
}

/// <summary>
/// 一轮"计划→执行"循环轨迹：计划步骤 + 每步结果 + 本轮收获证据 + 评估判定 + 是否换策略。
/// </summary>
public sealed record TraceCycle
{
    public int Number { get; init; }
    public string Goal { get; init; } = "";
    public IReadOnlyList<string> PlannedSteps { get; init; } = [];
    /// <summary>本轮的规划思考（LLM Planner 的 reasoning_content；规则规划为空）。</summary>
    public string? PlanReasoning { get; init; }
    public IReadOnlyList<CycleStepTrace> Steps { get; init; } = [];
    /// <summary>本轮新引出的证据摘要（[N] 会话：内容截断）。</summary>
    public IReadOnlyList<string> EvidenceGained { get; init; } = [];
    /// <summary>评估判定：answer / replan / stop + 原因。</summary>
    public string? Verdict { get; init; }
}

/// <summary>
/// 任务级 Trace（plan2 §11 / P19）：Task → 每轮 Plan/Step → 评估 → 终止原因。
/// AgentOrchestrator 在执行时记录，随 AgentResult 返回；不覆盖 P8（AgentLoop）旧 trace。
/// </summary>
public sealed record TaskTrace
{
    public string UserQuery { get; init; } = "";
    public IReadOnlyList<TraceCycle> Cycles { get; init; } = [];
    public bool Completed { get; init; }
    public string? TerminationReason { get; init; }
    public int TotalEvidence { get; init; }
    public double ElapsedMs { get; init; }

    /// <summary>渲染为多行文本（验收/日志/UI Trace 面板复用）。</summary>
    public string ToText()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("任务: ").Append(UserQuery).AppendLine();
        foreach (var c in Cycles)
        {
            sb.Append($"  [{c.Number}] 目标: {c.Goal} | 计划: [{string.Join(", ", c.PlannedSteps)}]").AppendLine();
            if (!string.IsNullOrWhiteSpace(c.PlanReasoning))
                AppendIndented(sb, "规划思考", c.PlanReasoning!, 6);
            foreach (var s in c.Steps)
            {
                sb.Append(s.Success
                    ? $"      ✓ {s.Skill}：{s.Summary}（{s.LatencyMs:0}ms）\n"
                    : $"      ✗ {s.Skill}：{s.Error ?? s.Summary}\n");
                if (!string.IsNullOrWhiteSpace(s.Reasoning))
                    AppendIndented(sb, "思考", s.Reasoning!, 8);
                foreach (var t in s.ToolCalls)
                    sb.Append(t.Success
                        ? $"        → 调用 {t.Name}({Compact(t.ArgumentsJson, 80)}) {t.LatencyMs:0}ms\n"
                        : $"        → 调用 {t.Name}({Compact(t.ArgumentsJson, 80)}) 失败: {t.Error}\n");
            }
            foreach (var ev in c.EvidenceGained)
                sb.Append($"      证据 {ev}\n");
            if (c.Verdict is not null)
                sb.Append($"      → 判定: {c.Verdict}\n");
        }
        if (Completed)
            sb.Append($"完成（{TotalEvidence} 条证据 / {ElapsedMs:0}ms）\n");
        else
            sb.Append($"终止: {TerminationReason}（{ElapsedMs:0}ms）\n");
        return sb.ToString().TrimEnd();
    }

    /// <summary>思考过程按行缩进输出（多行不换行糊成一团，方便人眼读）。</summary>
    private static void AppendIndented(System.Text.StringBuilder sb, string label, string text, int indent)
    {
        var pad = new string(' ', indent);
        var lines = text.Replace("\r", "").Split('\n');
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.Length > 0) sb.Append(pad).Append(label).Append(": ").Append(t).Append('\n');
        }
    }

    private static string Compact(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
