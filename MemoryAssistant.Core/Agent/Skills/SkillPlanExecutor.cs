using System.Diagnostics;
using MemoryAssistant.Core.Agent.Planner;
using MemoryAssistant.Core.Agent.Runtime;

namespace MemoryAssistant.Core.Agent.Skills;

/// <summary>单个 Skill 步骤的结构化结果（Trace/评估用）。</summary>
public sealed record SkillOutcome
{
    public string Skill { get; init; } = "";
    public bool Success { get; init; } = true;
    public string Summary { get; init; } = "";
    public string? Error { get; init; }
    public double LatencyMs { get; init; }
    /// <summary>本步模型的思考过程（仅展示用）。</summary>
    public string? Reasoning { get; init; }
    /// <summary>本步实际发出的工具调用（仅展示用）。</summary>
    public IReadOnlyList<ToolCallTrace> ToolCalls { get; init; } = [];
    /// <summary>本步 Skill 直接产出的答案（如 tools/mission 已自行组织好结论；非空时上层应优先采用）。</summary>
    public string? Draft { get; init; }
}

/// <summary>一次"按计划执行 Skill"的结果。</summary>
public sealed record PlanExecutionResult
{
    public bool RanToCompletion { get; init; }
    /// <summary>是否已有足够证据/结论可作答（供 P15 Evaluator 判断是否收尾或重规划）。</summary>
    public bool Sufficient { get; init; }
    /// <summary>已统一编号 1..N 的证据（本轮内编号）。</summary>
    public IReadOnlyList<Evidence> Evidence { get; init; } = [];
    /// <summary>每步执行摘要（Trace/任务状态展示）。</summary>
    public IReadOnlyList<string> StepSummaries { get; init; } = [];
    /// <summary>本轮实际执行过的 Skill 名（按顺序；供 Replanner 避免重复策略）。</summary>
    public IReadOnlyList<string> ExecutedSkills { get; init; } = [];
    /// <summary>逐 Skill 结构化结果（供 Task Trace / UI）。</summary>
    public IReadOnlyList<SkillOutcome> Outcomes { get; init; } = [];
    /// <summary>本轮 Skill 理解出的任务草案（对话式编排；非空时 UI 弹确认卡）。</summary>
    public Missions.MissionDraft? PendingMission { get; init; }
    public string? TerminationReason { get; init; }
    public double ElapsedMs { get; init; }
}

/// <summary>
/// 线性执行器（P13 版）：把 AgentPlan 的步骤逐个交给 SkillRegistry 执行，
/// 结果写回 TaskState（步骤/观测/证据），受 AgentBudget 约束。
/// P15 将在 Executor 之后插入 Evaluator/Replanner。
/// </summary>
public sealed class SkillPlanExecutor
{
    private readonly SkillRegistry _skills;
    private readonly Action<AgentProgress>? _onProgress;

    public SkillPlanExecutor(SkillRegistry skills, Action<AgentProgress>? onProgress = null)
    {
        _skills = skills;
        _onProgress = onProgress;
    }

    /// <summary>
    /// 能力 → 一句"正在做什么"。给长时间等待一个可见的交代（多步分析常常要跑 1~2 分钟），
    /// 直接用 Skill 的 Description 太长，不适合当状态行。
    /// </summary>
    private static string StatusOf(string skill) => skill switch
    {
        "recall" => "正在回忆相关聊天…",
        "stats" => "正在统计聊天量与活跃度…",
        "timeline" => "正在按时间线梳理…",
        "commitment" => "正在找没做完的承诺…",
        "topic" => "正在分析话题分布…",
        "profile" => "正在看人物/会话画像…",
        "wechat" => "正在读微信窗口当前聊天…",
        "tools" => "正在调用工具查聊天记录…",
        "mission" => "正在整理任务草案…",
        "action" => "正在准备微信写操作…",
        "expand" => "正在展开上一条…",
        _ => $"正在执行 {skill}…",
    };

    public async Task<PlanExecutionResult> ExecuteAsync(
        AgentPlan plan,
        AgentBudget budget,
        TaskState? state = null,
        CancellationToken ct = default,
        SkillRequest? request = null)
    {
        var skillRequest = new SkillRequest
        {
            Query = plan.Goal,
            Hint = request?.Hint,
            Window = request?.Window,
            // 会话上文要一路带到 Skill（尤其 tools）：否则"继续找他的最新记录"在规划器重述后就丢了对象
            OriginalQuery = request?.Query,
            LastPerson = request?.LastPerson,
            Focus = request?.Focus,
            History = request?.History ?? [],
            // 思考增量：让 Skill 内部（如 tools 的 ReAct 循环）能边想边推给 UI
            OnThinking = _onProgress is null ? null : d => _onProgress(AgentProgress.Thinking(d)),
            // 工具调用：原样透传，UI 单独凸出展示"调了什么、拿到什么"
            OnTool = _onProgress,
        };
        var sw = Stopwatch.StartNew();
        var allEvidence = new List<Evidence>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var summaries = new List<string>();
        var executedSkills = new List<string>();
        var outcomes = new List<SkillOutcome>();
        bool anySufficient = false;
        bool completed = true;
        string? termReason = null;
        Missions.MissionDraft? pendingMission = null;

        foreach (var step in plan.Steps)
        {
            ct.ThrowIfCancellationRequested();

            // finish：无需工具，直接作答
            if (step.Kind == PlanStepKind.Finish)
            {
                summaries.Add($"步骤 {step.Id}：直接回答。");
                anySufficient = true;
                continue;
            }

            // verify：本轮线性执行先记录为通过，交给后续 Evaluator/复读步骤验证。
            if (step.Kind == PlanStepKind.Verify)
            {
                summaries.Add($"步骤 {step.Id}：验证。");
                continue;
            }

            // 同一个能力在一次计划里跑两遍没有意义：Skill 不带参数，第二次只会拿到同样的结果，
            // 白烧一轮工具预算和几十秒。模型偶尔会排出重复步骤（实测 [tools, profile, topic, tools]）。
            if (executedSkills.Contains(step.Name, StringComparer.Ordinal))
            {
                summaries.Add($"步骤 {step.Id}：跳过重复的 {step.Name}（本轮已执行过）。");
                continue;
            }

            executedSkills.Add(step.Name);
            _onProgress?.Invoke(AgentProgress.Status(StatusOf(step.Name)));
            if (!budget.TryConsumeTool())
            {
                completed = false;
                termReason = $"skill_budget_exceeded（步骤 {step.Id}）";
                break;
            }
            if (!_skills.TryGet(step.Name, out var skill))
            {
                completed = false;
                termReason = $"skill_not_found:{step.Name}";
                if (state is not null) state.TerminationReason = termReason;
                break;
            }

            var taskStep = state?.AddStep(PlanStepKind.Skill, skill.Name, step.Reason)?.MarkRunning();
            SkillResult result;
            try
            {
                result = await skill.ExecuteAsync(skillRequest, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                taskStep?.Fail(ex.Message);
                outcomes.Add(new SkillOutcome { Skill = step.Name, Success = false, Error = ex.Message, LatencyMs = 0 });
                completed = false;
                termReason = $"skill_failed:{step.Name}:{ex.Message}";
                if (state is not null) state.TerminationReason = termReason;
                break;
            }
            if (!result.Success)
            {
                taskStep?.Fail(result.Error ?? "skill failed");
                outcomes.Add(new SkillOutcome { Skill = step.Name, Success = false, Error = result.Error, LatencyMs = result.ElapsedMs });
                completed = false;
                termReason = $"skill_failed:{step.Name}:{result.Error}";
                if (state is not null) state.TerminationReason = termReason;
                break;
            }

            taskStep?.Complete(result.Summary);
            outcomes.Add(new SkillOutcome
            {
                Skill = step.Name,
                Success = true,
                Summary = result.Summary,
                LatencyMs = result.ElapsedMs,
                Reasoning = result.Reasoning,
                ToolCalls = result.ToolCalls,
                Draft = result.Draft,
            });
            summaries.Add($"步骤 {step.Id} [{skill.Name}]：{result.Summary}");
            if (state is not null)
            {
                state.AddObservation(new Observation
                {
                    Source = skill.Name,
                    Summary = result.Summary,
                    Detail = result.Draft ?? string.Join("\n", result.Evidence.Take(3).Select(e => e.Content)),
                    Success = true,
                    LatencyMs = result.ElapsedMs,
                });
            }
            if (result.Sufficient) anySufficient = true;
            pendingMission ??= result.PendingMission;   // 首个草案即生效（一轮只编排一个任务）
            foreach (var e in result.Evidence)
            {
                var key = $"{e.SessionId}|{e.CreateTime}|{e.Content}";
                if (seen.Add(key)) allEvidence.Add(e);
            }
        }

        // 统一编号 1..N（Evidence.Index 保证 citation 稳定）
        var finalEvidence = allEvidence.Select((e, i) => e with { Index = i + 1 }).ToList();
        if (state is not null)
            foreach (var e in finalEvidence) state.AddEvidence(e);

        sw.Stop();
        return new PlanExecutionResult
        {
            RanToCompletion = completed,
            Sufficient = anySufficient,
            Evidence = finalEvidence,
            StepSummaries = summaries,
            ExecutedSkills = executedSkills,
            Outcomes = outcomes,
            PendingMission = pendingMission,
            TerminationReason = termReason,
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
        };
    }
}
