using System.Diagnostics;
using System.Text;
using MemoryAssistant.Core.Agent.Action;
using MemoryAssistant.Core.Agent.Answer;
using MemoryAssistant.Core.Agent.Evaluate;
using MemoryAssistant.Core.Agent.Planner;
using MemoryAssistant.Core.Agent.Replan;
using MemoryAssistant.Core.Agent.Runtime;
using MemoryAssistant.Core.Agent.Skills;
using MemoryAssistant.Core.Agent.Trace;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.Logging;

namespace MemoryAssistant.Core.Agent;

/// <summary>
/// Agent 总控（plan2 §7 主循环）：Plan → Execute(SkillPlanExecutor) → Evaluate → Replan → 直到
/// 足够作答 / 策略穷尽 / 预算用尽 / 重复循环 / 取消。作为 IAgentTaskExecutor 挂到 P11 TaskRuntime，
/// 从而状态可观察、取消可传播。旧 WorkflowEngine 不动（P11-E 保留），本类面向 2.0 主入口。
/// </summary>
public sealed class AgentOrchestrator : IAgentTaskExecutor
{
    private readonly IPlanner _planner;
    private readonly SkillRegistry _skills;
    private readonly AgentOptions _options;
    private readonly IEvaluator _evaluator;
    private readonly IReplanner _replanner;
    private readonly IAnswerComposer? _composer;
    private readonly IAppLogger? _logger;
    private readonly bool _enableLlmPlanning;
    private readonly Action<AgentProgress>? _onProgress;

    public AgentOrchestrator(
        IPlanner planner,
        SkillRegistry skills,
        AgentOptions options,
        IEvaluator? evaluator = null,
        IReplanner? replanner = null,
        IAppLogger? logger = null,
        bool enableLlmPlanning = false,
        IAnswerComposer? composer = null,
        Action<AgentProgress>? onProgress = null)
    {
        _planner = planner;
        _skills = skills;
        _options = options;
        _evaluator = evaluator ?? new RuleEvaluator();
        _replanner = replanner ?? new RuleReplanner();
        _logger = logger;
        _enableLlmPlanning = enableLlmPlanning;
        _composer = composer;
        _onProgress = onProgress;
    }

    public string Name => "agent-orchestrator";

    public async Task<AgentResult> ExecuteAsync(AgentTask task, AgentBudget budget, CancellationToken ct)
    {
        var query = task.UserQuery;
        var sw = Stopwatch.StartNew();
        var executor = new SkillPlanExecutor(_skills, _onProgress);
        var attemptedSkills = new HashSet<string>(StringComparer.Ordinal);
        var planSignatures = new HashSet<string>(StringComparer.Ordinal);
        var evidenceStore = new EvidenceStore(); // 任务级事实层：稳定编号 + 生命周期
        var traceCycles = new List<TraceCycle>(); // P19 Task Trace
        var maxReplan = _options.EnableReplanning ? Math.Max(0, _options.MaxReplanRounds) : 0;

        var plan = await _planner.PlanAsync(query, hint: task.Hint, preferLlm: _enableLlmPlanning, ct: ct,
            // 规划时的思考也要边生成边推：用户在"正在理解你的问题…"之后就能看到模型在权衡用哪个能力
            onReasoningDelta: _onProgress is null ? null : d => _onProgress(AgentProgress.Thinking(d)));
        planSignatures.Add(Signature(plan));
        var plannedSkills = plan.Steps.Where(s => s.Kind == PlanStepKind.Skill).Select(s => s.Name).ToList();
        if (plannedSkills.Count > 0)
            _onProgress?.Invoke(AgentProgress.Status($"已规划 {plannedSkills.Count} 个能力：{string.Join("、", plannedSkills)}"));

        // 写操作意图（第三阶段补充 §1/§3）：用户明确要求"发消息/回复"时，本轮必须真的发生一次对应工具调用。
        var requiredTool = WriteIntentGuard.RequiredTool(query, task.LastPerson);
        var writeCallTraces = new List<ToolCallTrace>();
        string? correctionDraft = null;
        if (requiredTool is not null)
            _logger?.Info($"[Orchestrator] 检测到写操作意图 → 本轮要求真的调用 {requiredTool}");

        bool answered = false;
        string? stopReason = null;
        int replanUsed = 0;
        int cycles = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            cycles++;

            var run = await executor.ExecuteAsync(plan, budget, task.State, ct,
                new SkillRequest
                {
                    Query = query,
                    Hint = task.Hint,
                    Window = task.Window,
                    // 会话上文：追问("继续找他的最新记录")要靠它才认得出说的是谁
                    LastPerson = task.LastPerson,
                    Focus = task.Focus,
                    History = task.History,
                    // 写操作意图 → 交给工具 Skill 用 tool_choice 钉死（§2）
                    RequiredTool = requiredTool,
                });
            foreach (var s in run.ExecutedSkills) attemptedSkills.Add(s);
            if (run.PendingMission is not null) task.State.PendingMission = run.PendingMission;
            writeCallTraces.AddRange(run.Outcomes.SelectMany(o => o.ToolCalls));

            // 证据入库：稳定编号 + 生命周期（来源为 Skill 产物 → Observed）
            var evidenceBefore = evidenceStore.Count;
            foreach (var ev in run.Evidence)
                evidenceStore.Observe(ev, originTool: run.ExecutedSkills.LastOrDefault());
            var gained = evidenceStore.Items.Skip(evidenceBefore)
                .Select(e => $"[{e.Index}] {e.SessionDisplayName}：{Truncate(e.Content, 40)}").ToList();

            var verdict = _evaluator.Evaluate(new EvaluationInput
            {
                Plan = plan,
                Run = run,
                ReplanRoundsUsed = replanUsed,
                MaxReplanRounds = maxReplan,
            });
            _logger?.Debug($"[Orchestrator] 第 {cycles} 轮 verdict={verdict.RecommendedAction}（{verdict.Reason}）");

            // 记录本轮 trace
            traceCycles.Add(new TraceCycle
            {
                Number = cycles,
                Goal = plan.Goal,
                PlannedSteps = plan.Steps.Select(s => $"{s.Kind}:{s.Name}").ToList(),
                PlanReasoning = plan.Reasoning,
                Steps = run.Outcomes.Select(o => new CycleStepTrace
                {
                    Skill = o.Skill,
                    Success = o.Success,
                    Summary = o.Summary,
                    Error = o.Error,
                    LatencyMs = o.LatencyMs,
                    Reasoning = o.Reasoning,
                    ToolCalls = o.ToolCalls,
                }).ToList(),
                EvidenceGained = gained,
                Verdict = $"{verdict.RecommendedAction}：{verdict.Reason}",
            });

            if (verdict.Sufficient || verdict.RecommendedAction != "replan")
            {
                answered = verdict.Sufficient;
                stopReason = verdict.Sufficient ? null : verdict.Reason;
                if (answered && evidenceStore.Count > 0)
                {
                    evidenceStore.VerifyAll(); // 判定足够 → 全部证据核实
                    evidenceStore.MarkCited(); // 写入回答 → 已引用
                }
                break;
            }

            // 换策略
            var next = _replanner.NextPlan(query, task.Hint, attemptedSkills);
            if (next is null)
            {
                stopReason = "没有更多可用策略（换策略已穷尽），如实终止";
                break;
            }
            var nextSig = Signature(next);
            if (planSignatures.Contains(nextSig))
            {
                stopReason = "strategy_stuck：检测到重复策略循环，停止避免空转";
                break;
            }
            planSignatures.Add(nextSig);
            plan = next;
            replanUsed++;
        }

        // ===== 纠正轮（第三阶段补充 §3）：写操作意图没有兑现 → 不允许就这么结束 =====
        // 判据：用户明确要求写操作（ActionIntentParser 命中），但整个计划跑完都没有一次**成功**的写操作工具调用
        // （既没调 wechat_send_message，也没调 wechat_open_chat）。
        // 这时强制补一轮 tools（并把 tool_choice 钉到该工具上）——模型必须真的动手；再失败就如实汇报。
        if (requiredTool is not null && !WriteIntentGuard.HasSuccessfulWriteCall(writeCallTraces)
            && !WriteIntentGuard.WasCancelledByUser(writeCallTraces)
            && !WriteIntentGuard.WasIndeterminate(writeCallTraces))
        {
            _logger?.Warn($"[Orchestrator] 用户要求写操作（{requiredTool}），但本轮没有成功的写操作工具调用 → 启动纠正轮。");
            _onProgress?.Invoke(AgentProgress.Status($"刚才那一步没有真的执行，正在按写操作重试（{requiredTool}）…"));

            var correctionPlan = new AgentPlan
            {
                Goal = $"真的执行这个写操作：{query}",
                Steps = [new PlanStep { Id = "fix-write", Kind = PlanStepKind.Skill, Name = "tools", Reason = "写操作必须真的调用工具（纠正轮）" }],
            };
            var fixRun = await executor.ExecuteAsync(correctionPlan, budget, task.State, ct,
                new SkillRequest
                {
                    Query = query,
                    Hint = task.Hint,
                    Window = task.Window,
                    LastPerson = task.LastPerson,
                    Focus = task.Focus,
                    History = task.History,
                    RequiredTool = requiredTool,
                });
            cycles++;
            writeCallTraces.AddRange(fixRun.Outcomes.SelectMany(o => o.ToolCalls));
            foreach (var ev in fixRun.Evidence) evidenceStore.Observe(ev, originTool: "tools");
            correctionDraft = fixRun.Outcomes.FirstOrDefault(o => !string.IsNullOrWhiteSpace(o.Draft))?.Draft;
            traceCycles.Add(new TraceCycle
            {
                Number = cycles,
                Goal = correctionPlan.Goal,
                PlannedSteps = ["skill:tools"],
                Steps = fixRun.Outcomes.Select(o => new CycleStepTrace
                {
                    Skill = o.Skill,
                    Success = o.Success,
                    Summary = o.Summary,
                    Error = o.Error,
                    LatencyMs = o.LatencyMs,
                    Reasoning = o.Reasoning,
                    ToolCalls = o.ToolCalls,
                }).ToList(),
                Verdict = WriteIntentGuard.HasSuccessfulWriteCall(writeCallTraces)
                    ? "纠正轮：写操作已真的执行"
                    : "纠正轮：仍然没有成功的写操作工具调用（只能如实汇报未执行）",
            });

            if (WriteIntentGuard.HasSuccessfulWriteCall(writeCallTraces))
            {
                answered = true;
                stopReason = null;
            }
            else
            {
                // 纠正轮也没兑现 → 本轮不算完成（UI 会显示"本轮提前结束"，回答里也会钉一句实话）
                answered = false;
                stopReason = $"写操作未执行：{requiredTool} 没有被成功调用";
            }
        }

        var answer = await ComposeAnswerAsync(task, query, evidenceStore, attemptedSkills.Count == 0, answered, stopReason, ct);

        // ===== 不许"假装执行"（§4）：用户要求写操作、但没有成功的工具结果 → 在回答前钉一句确定性的实话 =====
        // 为什么必须由 Runtime 保证：作答器是模型，模型完全可能把"没做"写成"做了"（实测出现过）。
        // 这里是最后一公里：不管模型怎么写，用户都会在第一时间看到"没有发出消息"。
        var cancelledByUser = WriteIntentGuard.WasCancelledByUser(writeCallTraces);
        var indeterminate = WriteIntentGuard.WasIndeterminate(writeCallTraces);
        if (requiredTool is not null && !WriteIntentGuard.HasSuccessfulWriteCall(writeCallTraces))
        {
            var why = indeterminate
                ? "屏幕上的证据只凑齐一半（可能已经发出去了），所以**我没有重发**——请你到微信里看一眼确认"
                : cancelledByUser
                    ? "你在确认框里选择了取消"
                    : WriteIntentGuard.HasAnyWriteCall(writeCallTraces)
                        ? "工具调用已发出，但没有成功（见上面的执行过程 / 工具结果）"
                        : "模型没有真的调用发消息工具（只生成了文字）";
            // "无法确认"不能说成"没有发出去"：那种情况消息很可能已经到对方手机上了
            var headline = indeterminate
                ? "（说明：这条消息**是否发出去了我无法确认**"
                : "（说明：这条消息**没有发出去**";
            answer = $"{headline} —— {why}。）\n\n" + answer;
        }
        else if (requiredTool is not null && correctionDraft is not null && !string.IsNullOrWhiteSpace(correctionDraft))
        {
            // 纠正轮真的有工具结果：优先采用工具轮自己组织的答案（它含真实的发送结果）
            answer = correctionDraft;
        }

        if (answered) task.State.Conclusion = answer;
        sw.Stop();
        return new AgentResult
        {
            Answer = answer,
            CompletedNormally = answered,
            RoundCount = cycles,
            EarlyStopReason = answered ? null : stopReason,
            Evidence = answered ? evidenceStore.Items.ToList() : [],
            PendingMission = task.State.PendingMission,
            TaskTrace = new TaskTrace
            {
                UserQuery = query,
                Cycles = traceCycles,
                Completed = answered,
                TerminationReason = answered ? null : stopReason,
                TotalEvidence = evidenceStore.Count,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
            },
            TotalElapsedMs = sw.Elapsed.TotalMilliseconds,
        };
    }

    private static string Signature(AgentPlan plan)
        => string.Join("|", plan.Steps.Select(s => $"{s.Kind}:{s.Name}"));

    /// <summary>
    /// 组织回答：先让作答器（LLM）把已查证的材料讲成人话；未装配/失败/无材料时，
    /// 用确定性文案兜底（原始记录摘录 + 分析结论），保证"回答"这一步永不空白。
    /// </summary>
    private async Task<string> ComposeAnswerAsync(
        AgentTask task, string query, EvidenceStore store, bool directReply,
        bool answered, string? stopReason, CancellationToken ct)
    {
        // tools Skill 已经产出"模型自己组织的、含工具结果的答案"（SkillResult.Draft）。
        // 修复前的行为：CollectNotes 显式跳过 tools，BuildAnswer 又把观察到的 Draft 截断到 240 字，
        // 于是模型明明把工具调通了，用户看到的却是一段残缺摘录——"工具调用能力"死在最后一公里。
        // 这里在没有证据可引用时直接采用该 Draft（它本身就是最终答案），不再二次加工。
        var toolDraft = task.State.Observations
            .LastOrDefault(o => o.Source == "tools" && !string.IsNullOrWhiteSpace(o.Detail))?.Detail;
        if (store.Count == 0 && !string.IsNullOrWhiteSpace(toolDraft))
        {
            _logger?.Debug("[Orchestrator] 采用 tools Skill 的直接产出作为回答（无证据需引用）。");
            return toolDraft!;
        }

        var fallback = BuildAnswer(store, task, answered, stopReason);
        if (_composer is null) return fallback;

        _onProgress?.Invoke(AgentProgress.Status("正在把结果讲清楚…"));
        try
        {
            var composed = await _composer.ComposeAsync(new AnswerRequest
            {
                UserQuery = task.UserQuery,
                Goal = query,
                Evidence = store.Items,
                Notes = CollectNotes(task.State),
                History = task.History,
                LastPerson = task.LastPerson,
                Focus = task.Focus,
                Completed = answered,
                StopReason = stopReason,
                DirectReply = directReply,
            }, ct, onDelta: _onProgress is null ? null : d => _onProgress(AgentProgress.Answer(d)));
            if (!string.IsNullOrWhiteSpace(composed)) return composed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[Orchestrator] 作答环节失败，改用原始记录作答：{ex.Message}");
        }
        return fallback;
    }

    /// <summary>收集分析类 Skill 的结论文本（recall 不进来：它的信息已经在证据里）。
    /// tools 的 Draft 是"模型自己组织的答案"，也作为结论交给作答器润色。</summary>
    private static List<string> CollectNotes(TaskState state)
    {
        var notes = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in state.Observations)
        {
            if (o.Source is "recall" or "mission") continue;

            string text;
            if (o.Source == "tools")
            {
                // tools 的产出是模型自己写的分析草稿：**保留换行与层次**——压成一行再截断会把推理结构毁掉，
                // 作答器就没法"照着讲清楚"了。标上"分析草稿"让作答器知道这是已经做过的分析。
                text = (o.Detail ?? o.Summary).Replace("\r", "").Trim();
                if (text.Length > 0) text = "分析草稿：\n" + text;
            }
            else
            {
                text = (o.Detail ?? o.Summary).Replace("\r", " ").Replace("\n", " ").Trim();
            }

            if (text.Length == 0) continue;
            if (seen.Add(text)) notes.Add(text);
            if (notes.Count >= 6) break;
        }
        return notes;
    }

    /// <summary>
    /// 确定性兜底文案：不做假总结（那是模型的事），但也不能把原始素材原样倾倒。
    /// 按会话归类、控制条数与长度，并说明这是原始摘录。
    /// </summary>
    private static string BuildAnswer(EvidenceStore store, AgentTask task, bool answered, string? stopReason)
    {
        var sb = new StringBuilder();

        if (store.Count > 0)
        {
            sb.Append("我在你的聊天记录里找到这些相关内容：\n");
            var bySession = store.Items
                .GroupBy(e => string.IsNullOrWhiteSpace(e.SessionDisplayName) ? e.SessionId : e.SessionDisplayName)
                .Take(3);
            foreach (var g in bySession)
            {
                sb.Append("· ").Append(g.Key).Append('\n');
                foreach (var e in g.OrderByDescending(x => x.CreateTime).Take(3))
                {
                    var when = e.CreateTime > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(e.CreateTime).ToLocalTime().ToString("MM-dd HH:mm")
                        : "";
                    var who = string.IsNullOrWhiteSpace(e.SenderName) ? "" : e.SenderName + "：";
                    sb.Append("   [").Append(e.Index).Append("] ").Append(when).Append(' ').Append(who)
                      .Append(Quote(e.Content)).Append('\n');
                }
            }
            if (store.Count > 9) sb.Append($"（共 {store.Count} 条相关记录。）\n");
        }
        else
        {
            foreach (var o in task.State.Observations)
            {
                var text = (o.Detail ?? o.Summary).Replace("\r", " ").Replace("\n", " ").Trim();
                if (text.Length == 0) continue;
                sb.Append("· ").Append(Truncate(text, 240)).Append('\n');
            }
        }

        if (!answered)
            sb.Append($"（这次没能给出可靠结论：{stopReason}）");

        return sb.ToString().TrimEnd();
    }

    /// <summary>原文摘录：去掉换行、按句读截断，避免出现"半个词 + 下一条消息"的糊成一团。</summary>
    private static string Quote(string content)
    {
        var text = content.Replace("\r", " ").Replace("\n", " ").Trim();
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s{2,}", " ");
        return Truncate(text, 70);
    }

    private static string Truncate(string s, int max)
    {
        s = s.Replace("\n", " ");
        return s.Length <= max ? s : s[..max] + "…";
    }
}
