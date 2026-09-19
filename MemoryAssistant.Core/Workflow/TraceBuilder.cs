namespace MemoryAssistant.Core.Workflow;

/// <summary>trace 行级别。</summary>
public enum TraceLevel { Info, Warning, Error }

/// <summary>扁平化 trace 步骤（UI 与控制台共用）。</summary>
public sealed record TraceStep(string Category, string Text, TraceLevel Level);

/// <summary>把 WorkflowResult 扁平化为 trace 步骤序列（纯函数，可单测）。</summary>
public static class TraceBuilder
{
    /// <summary>阶段事件 → 展示类别。</summary>
    private static readonly Dictionary<string, string> StageCategory = new()
    {
        ["intent"] = "intent",
        ["chitchat"] = "intent",
        ["coarse_retrieval"] = "retrieval",
        ["precise_read"] = "retrieval",
        ["scan_commitments"] = "retrieval",
        ["evidence"] = "evidence",
        ["fallback"] = "retrieval",
        ["llm"] = "llm",
    };

    public static IReadOnlyList<TraceStep> Build(WorkflowResult result)
    {
        var steps = new List<TraceStep>();

        // 阶段事件（intent/retrieval/evidence/llm）
        foreach (var ev in result.StageEvents)
        {
            var cat = StageCategory.TryGetValue(ev.Stage, out var c) ? c : ev.Stage;
            var level = ev.Detail.Contains("失败", StringComparison.Ordinal) ||
                        ev.Detail.Contains("异常", StringComparison.Ordinal)
                ? TraceLevel.Error
                : TraceLevel.Info;
            steps.Add(new TraceStep(cat, $"[{cat}] {ev.Detail}", level));
        }

        // 逐轮 + 逐工具
        if (result.AgentResult is { } agent)
        {
            foreach (var r in agent.Rounds)
            {
                steps.Add(new TraceStep("llm",
                    $"第 {r.Round} 轮 | {r.LatencyMs:0}ms | prompt {r.PromptTokens} | completion {r.CompletionTokens} | finish={r.FinishReason}",
                    TraceLevel.Info));
                foreach (var t in r.ToolCalls)
                {
                    var args = ArgsSnippet(t.ArgumentsJson);
                    steps.Add(t.Success
                        ? new TraceStep("tool", $"[✓] {t.Name}({args}) {t.LatencyMs:0}ms", TraceLevel.Info)
                        : new TraceStep("tool", $"[✗] {t.Name}({args}) 失败: {t.Error}", TraceLevel.Error));
                }
            }

            // 合计
            var tokens = agent.TotalPromptTokens == 0 && agent.TotalCompletionTokens == 0
                ? "未统计"
                : $"tokens {agent.TotalPromptTokens}+{agent.TotalCompletionTokens}";
            steps.Add(new TraceStep("llm",
                $"合计 {agent.Rounds.Count} 轮 | {tokens} | 总耗时 {result.TotalElapsedMs:0}ms",
                TraceLevel.Info));
        }
        else
        {
            steps.Add(new TraceStep("error", "Agent 轨迹缺失（AgentResult 为空）", TraceLevel.Error));
        }

        return steps;
    }

    private static string ArgsSnippet(string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson) || argsJson is "{}") return "";
        var s = argsJson.Trim();
        return s.Length <= 80 ? s : s[..80] + "...";
    }

    /// <summary>
    /// P20：把 2.0 Agent 路径的 TaskTrace（规划思考 → plan → 执行 → 工具调用 → 判定 → 终止）
    /// 扁平化为 UI Trace 步骤。思考过程仅在调试 Trace 面板展示，不参与任何后续模型请求。
    /// </summary>
    public static IReadOnlyList<TraceStep> Build(MemoryAssistant.Core.Agent.Trace.TaskTrace? trace)
    {
        if (trace is null)
            return [new TraceStep("error", "Trace 缺失", TraceLevel.Error)];

        var steps = new List<TraceStep>();
        foreach (var c in trace.Cycles)
        {
            steps.Add(new TraceStep("plan",
                $"[{c.Number}] {c.Goal} | 计划: [{string.Join(", ", c.PlannedSteps)}]", TraceLevel.Info));
            AppendThinking(steps, c.PlanReasoning, "plan");
            foreach (var s in c.Steps)
            {
                steps.Add(s.Success
                    ? new TraceStep("tool", $"[✓] {s.Skill}：{s.Summary}（{s.LatencyMs:0}ms）", TraceLevel.Info)
                    : new TraceStep("tool", $"[✗] {s.Skill}：{s.Error ?? s.Summary}", TraceLevel.Error));
                foreach (var t in s.ToolCalls)
                {
                    var args = ArgsSnippet(t.ArgumentsJson);
                    steps.Add(t.Success
                        ? new TraceStep("tool", $"[✓] 调用 {t.Name}({args}) {t.LatencyMs:0}ms", TraceLevel.Info)
                        : new TraceStep("tool", $"[✗] 调用 {t.Name}({args}) 失败: {t.Error}", TraceLevel.Error));
                    if (t.Success && !string.IsNullOrWhiteSpace(t.Output))
                        steps.Add(new TraceStep("tool", $"    → {Compact(t.Output, 120)}", TraceLevel.Info));
                }
                AppendThinking(steps, s.Reasoning, "think");
            }
            foreach (var gained in c.EvidenceGained.Take(8))
                steps.Add(new TraceStep("evidence", $"证据 {gained}", TraceLevel.Info));
            if (c.EvidenceGained.Count > 8)
                steps.Add(new TraceStep("evidence", $"…另有 {c.EvidenceGained.Count - 8} 条证据", TraceLevel.Info));
            if (c.Verdict is not null)
                steps.Add(new TraceStep("evaluate", c.Verdict, TraceLevel.Info));
        }
        steps.Add(trace.Completed
            ? new TraceStep("finish", $"完成（{trace.TotalEvidence} 条证据 / {trace.ElapsedMs:0}ms）", TraceLevel.Info)
            : new TraceStep("error", $"终止: {trace.TerminationReason}（{trace.ElapsedMs:0}ms）", TraceLevel.Error));
        return steps;
    }

    /// <summary>思考过程按行拆成独立 Trace 步骤（一条长文本在 UI 里读不动）。</summary>
    private static void AppendThinking(List<TraceStep> steps, string? reasoning, string category)
    {
        if (string.IsNullOrWhiteSpace(reasoning)) return;
        foreach (var raw in reasoning.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0)
                steps.Add(new TraceStep(category, $"思考: {line}", TraceLevel.Info));
        }
    }

    private static string Compact(string s, int max)
    {
        var text = s.Replace("\n", " ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}
