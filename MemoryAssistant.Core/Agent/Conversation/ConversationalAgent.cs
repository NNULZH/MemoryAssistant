using MemoryAssistant.Core.Agent.Answer;
using MemoryAssistant.Core.Agent.Planner;
using MemoryAssistant.Core.Agent.Query;
using MemoryAssistant.Core.Agent.Runtime;
using MemoryAssistant.Core.Agent.Skills;
using MemoryAssistant.Core.Agent.Trace;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.Logging;

namespace MemoryAssistant.Core.Agent.Conversation;

/// <summary>
/// 多轮会话门面（plan2 §9 / P17 + P18）：单次调用 = 目标抽取(QueryModel) →
/// 追问解析(FollowUpResolver) → 建任务(带 Hint) → TaskRuntime+AgentOrchestrator 执行 →
/// 记录回合（人/焦点/证据/目标模型）到会话记忆。
/// 让"他/那次/还有吗/第 N 条"能利用上一轮上下文，而不是从零重跑。
/// </summary>
public sealed class ConversationalAgent
{
    private readonly IPlanner _planner;
    private readonly SkillRegistry _skills;
    private readonly AgentOptions _options;
    private readonly IAppLogger? _logger;
    private readonly bool _enableLlmPlanning;
    private readonly IQueryAnalyzer _analyzer;
    private readonly Answer.IAnswerComposer? _composer;

    public ConversationalAgent(
        IPlanner planner,
        SkillRegistry skills,
        AgentOptions options,
        IAppLogger? logger = null,
        bool enableLlmPlanning = false,
        IQueryAnalyzer? analyzer = null,
        Answer.IAnswerComposer? composer = null)
    {
        _planner = planner;
        _skills = skills;
        _options = options;
        _logger = logger;
        _enableLlmPlanning = enableLlmPlanning;
        _analyzer = analyzer ?? new RuleQueryAnalyzer();
        _composer = composer;
    }

    public ConversationSession Session { get; } = new();

    /// <summary>喂给作答器的上文字数上限（最近 3 轮；越多越贵，且长上下文里模型反而会忽略早期内容）。</summary>
    private const int HistoryTurns = 3;

    /// <param name="ct">取消令牌。</param>
    /// <param name="onProgress">
    /// 进度回调（可选）：状态与答案增量会边跑边推给 UI，不必等整轮结束。
    /// 放在 ct 之后是为了不破坏既有调用（保持既有位置参数不变）。
    /// </param>
    public async Task<AgentResult> RunAsync(
        string userQuery, CancellationToken ct = default, Action<AgentProgress>? onProgress = null)
    {
        var model = _analyzer.Analyze(userQuery);
        var resolution = FollowUpResolver.Resolve(userQuery, Session);
        _logger?.Info(resolution.Note is null
            ? $"[会话] 用户：{userQuery} | 目标={model.IntentHint}"
            : $"[会话] 用户：{userQuery} → {resolution.Note}");

        // "展开第 N 条"：这条证据上一轮就已经在手里了，直接讲，不必再检索一遍
        if (resolution.DirectEvidence is { } hit)
            return await ExpandAsync(userQuery, hit, resolution.Note, ct, onProgress);

        // 有效 Hint：追问改写优先用改写结果；全新问题用本轮目标抽取结果（避免沿用上一轮旧 Hint 误导）
        var effectiveHint = resolution.Changed ? resolution.Hint : model.ToHint();
        // 追问里没点名时，把会话记忆里的人补进 hint：否则"继续找他的最新记录"在规划层就丢了对象
        effectiveHint = WithPerson(effectiveHint, Session.LastPerson);
        var task = new AgentTask(resolution.Query)
        {
            Hint = effectiveHint,
            Window = TimeWindowResolver.Resolve(model),   // V3.3：时间线索 → 检索时间窗
            History = RecentHistory(),
            LastPerson = Session.LastPerson,
            Focus = Session.Focus,
        };
        var rt = new TaskRuntime(_options, _logger);
        var orchestrator = new AgentOrchestrator(
            _planner, _skills, _options,
            logger: _logger,
            enableLlmPlanning: _enableLlmPlanning,
            composer: _composer,
            onProgress: onProgress);
        var done = await rt.RunAsync(task, orchestrator, ct);

        // 任务失败/取消时 task.Result 为 null。以前这里直接 done.Result! 解引用——一旦执行层抛异常
        // （最典型的是超过 MaxTaskSeconds 被取消）就炸成 "Object reference not set"，把真正的失败原因
        // 整个吞掉，用户只看到一句 NRE。现在如实回报原因，并给一个可操作的下一步。
        if (done.Result is not { } result)
        {
            var reason = done.Error ?? done.StatusMessage ?? done.Status.ToString();
            _logger?.Warn($"[会话] 任务未产出结果（{done.Status}）：{reason}");
            var failed = new AgentResult
            {
                Answer = $"这次没能查完：{reason}。可以缩小范围再问一次（比如指定某个人、某个群或一段时间）。",
                CompletedNormally = false,
                EarlyStopReason = reason,
                TotalElapsedMs = done.ElapsedMs,
            };
            Session.RecordTurn(userQuery, resolution.Query, failed.Answer, failed.Evidence,
                resolution.Note, effectiveHint, model);
            return failed;
        }

        // 兜底：作答器与确定性文案都没能产出内容时，至少别给用户一个空气泡。
        // 注意不能只在 CompletedNormally 时兜底——提前终止（超时/预算/工具全失败）反而更容易没内容。
        if (string.IsNullOrWhiteSpace(result.Answer))
            result = result with
            {
                Answer = result.CompletedNormally
                    ? "我这边没找到可作答的内容。可以换个说法，或告诉我更具体的人名/时间范围，我再查一次。"
                    : $"这次没能查完：{result.EarlyStopReason ?? "未产生可用结论"}。可以缩小范围再问一次（比如指定某个人、某个群或一段时间）。",
            };
        Session.RecordTurn(userQuery, resolution.Query, result.Answer, result.Evidence, resolution.Note, effectiveHint, model);
        return result;
    }

    /// <summary>
    /// 把会话记忆里的人补进 Hint：本轮句子没点名（"继续找他的最新记录"）时，
    /// 规划层就靠这个 Entity 知道对象是谁，否则会退化成"随便找找"。
    /// </summary>
    private static PlannerHint? WithPerson(PlannerHint? hint, string? person)
    {
        if (string.IsNullOrWhiteSpace(person)) return hint;
        if (hint is null) return new PlannerHint(null, person, null);
        return string.IsNullOrWhiteSpace(hint.Entity) ? hint with { Entity = person } : hint;
    }

    /// <summary>最近几轮问答（压缩），让作答器接得上"他/那条/刚才说的"。</summary>
    private List<Answer.AnswerTurn> RecentHistory()
        => Session.Turns
            .TakeLast(HistoryTurns)
            .Select(t => new Answer.AnswerTurn(t.OriginalQuery, t.Answer))
            .ToList();

    /// <summary>
    /// 直接展开上一轮已有的一条证据（不重新检索）：
    /// 走完整的作答器（带上文），拿不到模型输出时退回"把这条原文完整给出来"的确定性文案。
    /// </summary>
    private async Task<AgentResult> ExpandAsync(
        string userQuery, Evidence evidence, string? note, CancellationToken ct,
        Action<AgentProgress>? onProgress = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? answer = null;
        if (_composer is not null)
        {
            try
            {
                answer = await _composer.ComposeAsync(new AnswerRequest
                {
                    UserQuery = userQuery,
                    Goal = note ?? "展开上一条记录，把内容说清楚",
                    Evidence = [evidence],
                    History = RecentHistory(),
                    LastPerson = Session.LastPerson,
                    Focus = Session.Focus,
                }, ct, onDelta: onProgress is null ? null : d => onProgress(AgentProgress.Answer(d)));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn($"[会话] 展开作答失败，改用原文直出：{ex.Message}");
            }
        }

        answer = string.IsNullOrWhiteSpace(answer) ? DescribeEvidence(evidence) : answer;
        sw.Stop();

        var sessionName = string.IsNullOrEmpty(evidence.SessionDisplayName) ? evidence.SessionId : evidence.SessionDisplayName;
        var result = new AgentResult
        {
            Answer = answer,
            CompletedNormally = true,
            RoundCount = 1,
            Evidence = [evidence],
            TotalElapsedMs = sw.Elapsed.TotalMilliseconds,
            TaskTrace = new TaskTrace
            {
                UserQuery = userQuery,
                Completed = true,
                TotalEvidence = 1,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Cycles =
                [
                    new TraceCycle
                    {
                        Number = 1,
                        Goal = $"展开上一条（{sessionName}）",
                        PlannedSteps = ["expand:last-evidence"],
                        Steps =
                        [
                            new CycleStepTrace
                            {
                                Skill = "expand",
                                Success = true,
                                Summary = "复用上一轮证据，未重新检索",
                                LatencyMs = sw.Elapsed.TotalMilliseconds,
                            },
                        ],
                        EvidenceGained = [$"[{evidence.Index}] {sessionName}：{Compact(evidence.Content, 40)}"],
                        Verdict = "answer：上下文都在手里，直接说清楚即可",
                    },
                ],
            },
        };
        Session.RecordTurn(userQuery, userQuery, result.Answer, result.Evidence, note);
        return result;
    }

    private static string DescribeEvidence(Evidence e)
    {
        var session = string.IsNullOrEmpty(e.SessionDisplayName) ? e.SessionId : e.SessionDisplayName;
        var when = e.CreateTime > 0
            ? DateTimeOffset.FromUnixTimeSeconds(e.CreateTime).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "";
        var who = string.IsNullOrWhiteSpace(e.SenderName) ? "" : e.SenderName + "：";
        return $"第 {e.Index} 条来自「{session}」{when}\n{who}{e.Content}";
    }

    private static string Compact(string s, int max)
    {
        var text = s.Replace("\n", " ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}
