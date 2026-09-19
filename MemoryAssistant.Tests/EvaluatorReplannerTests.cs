using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Evaluate;
using MemoryAssistant.Core.Agent.Planner;
using MemoryAssistant.Core.Agent.Replan;
using MemoryAssistant.Core.Agent.Runtime;
using MemoryAssistant.Core.Agent.Skills;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.RAG;

namespace MemoryAssistant.Tests;

/// <summary>
/// P15 Evaluator/Replanner 单测：闭环（计划→执行→评估→重规划）、
/// 失败恢复、预算停止、策略穷尽、循环守卫——全部 FakeBackend 确定性执行，零 LLM/零数据。
/// </summary>
public sealed class EvaluatorReplannerTests
{
    private sealed class FakeBackend : IMemoryBackend
    {
        public IReadOnlyList<RetrievedChunk> Chunks { get; init; } = [];
        public IReadOnlyList<DayActivity> Days { get; init; } = [];
        public IReadOnlyList<Evidence> Snippets { get; init; } = [];
        public IReadOnlyList<Evidence> Commitments { get; init; } = [];
        public string StatsText { get; init; } = "";
        public bool ThrowOnSemantic { get; init; }

        public Task<IReadOnlyList<RetrievedChunk>> SemanticSearchAsync(string query, int topK, CancellationToken ct)
        {
            if (ThrowOnSemantic) throw new InvalidOperationException("RAG 服务不可用");
            return Task.FromResult(Chunks);
        }

        public Task<IReadOnlyList<Evidence>> ReadDayMessagesAsync(string sessionId, string date, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Evidence>>([]);

        public Task<IReadOnlyList<Evidence>> KeywordSearchAsync(string keyword, string? sessionId, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Evidence>>([]);

        public Task<string> GetStatsTextAsync(int limit, int sessionLimit, CancellationToken ct) => Task.FromResult(StatsText);

        public Task<IReadOnlyList<DayActivity>> GetRecentDaysAsync(int count, CancellationToken ct) => Task.FromResult(Days);

        public Task<IReadOnlyList<Evidence>> GetDaySnippetsAsync(string date, CancellationToken ct) => Task.FromResult(Snippets);

        public Task<IReadOnlyList<Evidence>> ScanCommitmentsAsync(int limit, CancellationToken ct) => Task.FromResult(Commitments);

        public Task<string> GetTopicsTextAsync(int recentDays, CancellationToken ct) => Task.FromResult("");

        public Task<string> GetProfilesTextAsync(int top, CancellationToken ct) => Task.FromResult("");
    }

    private static AgentOptions EcoOpts(int maxTools = 20, int maxReplan = 3, bool enableReplan = true) => new()
    {
        MaxToolCalls = maxTools,
        MaxReplanRounds = maxReplan,
        EnableReplanning = enableReplan,
        MaxTaskSeconds = 0,
    };

    private static async Task<AgentResult> RunAsync(string query, IMemoryBackend backend, AgentOptions opts, IReplanner? replanner = null)
    {
        var catalog = SkillCatalog.Default();
        var planner = new Planner(catalog, opts, chat: null);
        var skills = SkillRegistry.BuildDefault(backend);
        var orchestrator = new AgentOrchestrator(planner, skills, opts, replanner: replanner);

        var task = new AgentTask(query);
        var rt = new TaskRuntime(opts);
        var done = await rt.RunAsync(task, orchestrator, CancellationToken.None);
        return done.Result!;
    }

    private static RetrievedChunk Chunk(string sid, string text) => new() { SessionId = sid, SessionName = sid, Date = "2026-08-01", Text = text, MsgCount = 1 };

    // ---------- 场景 A：单策略足够，直接作答 ----------

    [Fact]
    public async Task ScenarioA_RecallSufficient_AnswersImmediately()
    {
        var backend = new FakeBackend { Chunks = [Chunk("s1", "秋招有消息了吗")] };
        var r = await RunAsync("我和同学聊过秋招吗？", backend, EcoOpts());

        Assert.True(r.CompletedNormally);
        Assert.Contains("[1]", r.Answer);
        Assert.Contains("秋招", r.Answer);
    }

    // ---------- 场景 B：策略不足 → 换策略（recall → timeline） ----------

    [Fact]
    public async Task ScenarioB_InsufficientRecall_ReplansToTimeline()
    {
        var backend = new FakeBackend
        {
            Days = [new DayActivity("2026-09-08", 120)],
            Snippets = [new Evidence { SessionId = "s1", SessionDisplayName = "同学群", Content = "那天聊到答辩安排", Source = "时间线" }],
        };
        var r = await RunAsync("我上次和朋友聊了什么？", backend, EcoOpts());

        Assert.True(r.CompletedNormally);
        Assert.Contains("[1]", r.Answer);
        Assert.Contains("答辩安排", r.Answer);
    }

    // ---------- 场景 C：策略达到重规划上限 → 诚实终止 ----------

    [Fact]
    public async Task ScenarioC_ReplanLimitExceeded_StopsHonestly()
    {
        var backend = new FakeBackend(); // 全部空 → recall 不足，timeline 也拿不到证据
        var r = await RunAsync("我上次和朋友聊了什么？", backend, EcoOpts(maxReplan: 1));

        Assert.False(r.CompletedNormally);
        Assert.NotNull(r.EarlyStopReason);
        Assert.Contains("上限", r.EarlyStopReason);
    }

    // ---------- 场景 D：工具失败 → 换策略恢复 ----------

    [Fact]
    public async Task ScenarioD_RecallThrows_ReplansAndRecovers()
    {
        var backend = new FakeBackend
        {
            ThrowOnSemantic = true,
            Days = [new DayActivity("2026-09-08", 5)],
            Snippets = [new Evidence { SessionId = "s2", SessionDisplayName = "老王", Content = "最近聊的都是实验", Source = "时间线" }],
        };
        var r = await RunAsync("我最近和朋友聊什么", backend, EcoOpts());

        Assert.True(r.CompletedNormally, r.EarlyStopReason ?? r.Answer);
        Assert.Contains("[1]", r.Answer);
    }

    // ---------- 场景 E：预算耗尽 → 如实终止（不空转） ----------

    [Fact]
    public async Task ScenarioE_BudgetExhausted_StopsHonestly()
    {
        // stats+timeline 两步计划：stats 拿不到数据（不足），预算只有 1 → timeline 触发 budget 停止
        var backend = new FakeBackend(); // StatsText 为空 → stats 不足
        var r = await RunAsync("我最近是不是经常半夜和人聊天？", backend, EcoOpts(maxTools: 1));

        Assert.False(r.CompletedNormally);
        // 预算数字属于轨迹信息，不再塞进给用户看的答案；答案只如实交代"没查清 + 原因"
        Assert.Contains("这次没能给出可靠结论", r.Answer);
        Assert.Contains("budget", r.Answer);
        Assert.DoesNotContain("工具已用", r.Answer);
        Assert.NotNull(r.EarlyStopReason);
        Assert.Contains("budget", r.EarlyStopReason);
    }

    // ---------- 场景 F：重复策略 → strategy_stuck 循环守卫 ----------

    [Fact]
    public async Task ScenarioF_RepeatedPlan_TriggersLoopGuard()
    {
        var backend = new FakeBackend();
        var stuckReplanner = new FixedReplanner(); // 永远返回与初始相同的 recall 计划
        var r = await RunAsync("我上次和朋友聊了什么？", backend, EcoOpts(), replanner: stuckReplanner);

        Assert.False(r.CompletedNormally);
        Assert.NotNull(r.EarlyStopReason);
        Assert.Contains("strategy_stuck", r.EarlyStopReason);
    }

    private sealed class FixedReplanner : IReplanner
    {
        public AgentPlan? NextPlan(string goal, PlannerHint? hint, IReadOnlyCollection<string> attemptedSkills)
            => new()
            {
                Goal = goal,
                FromRules = true,
                Steps = [new PlanStep { Id = "x1", Kind = PlanStepKind.Skill, Name = "recall" }],
            };
    }

    // ---------- Evaluator / Replanner 单元 ----------

    [Fact]
    public void RuleEvaluator_SufficientRun_RecommendsAnswer()
    {
        var eval = new RuleEvaluator();
        var run = new PlanExecutionResult
        {
            RanToCompletion = true,
            Sufficient = true,
            Evidence = [new Evidence { Index = 1, Content = "x" }],
        };
        var v = eval.Evaluate(new EvaluationInput { Plan = new AgentPlan { Steps = [] }, Run = run });
        Assert.True(v.Sufficient);
        Assert.Equal("answer", v.RecommendedAction);
        Assert.True(v.Confidence > 0.5);
    }

    [Fact]
    public void RuleEvaluator_BudgetStop_NotRecoverable()
    {
        var eval = new RuleEvaluator();
        var run = new PlanExecutionResult
        {
            RanToCompletion = false,
            Sufficient = false,
            TerminationReason = "skill_budget_exceeded（步骤 s2）",
        };
        var v = eval.Evaluate(new EvaluationInput { Plan = new AgentPlan { Steps = [] }, Run = run, MaxReplanRounds = 5 });
        Assert.Equal("stop", v.RecommendedAction);
    }

    [Fact]
    public void RuleEvaluator_SkillFailure_IsRecoverable()
    {
        var eval = new RuleEvaluator();
        var run = new PlanExecutionResult
        {
            RanToCompletion = false,
            Sufficient = false,
            TerminationReason = "skill_failed:recall:RAG 不可用",
            ExecutedSkills = ["recall"],
        };
        var v = eval.Evaluate(new EvaluationInput { Plan = new AgentPlan { Steps = [] }, Run = run, MaxReplanRounds = 3 });
        Assert.Equal("replan", v.RecommendedAction);
    }

    [Fact]
    public void RuleReplanner_AvoidsAttemptedSkills()
    {
        var replanner = new RuleReplanner();
        var next = replanner.NextPlan("我上次和朋友聊了什么？", hint: null, attemptedSkills: ["recall"]);
        Assert.NotNull(next);
        Assert.NotEqual("recall", next!.Steps[0].Name); // 不再重复 recall
        Assert.Contains(next.Steps[0].Name, new[] { "timeline", "stats", "profile", "topic", "commitment" });
    }

    [Fact]
    public void RuleReplanner_ReturnsNull_WhenAllTried()
    {
        var replanner = new RuleReplanner();
        // 内建记忆能力 + 兜底的 tools 都试过 → 诚实终止（返回 null 交上层收尾）
        var all = new[] { "recall", "timeline", "stats", "profile", "topic", "commitment", "tools" };
        Assert.Null(replanner.NextPlan("q", hint: null, attemptedSkills: all));
    }

    [Fact]
    public void RuleReplanner_FallsBackToTools_WhenMemorySkillsExhausted()
    {
        var replanner = new RuleReplanner();
        var all = new[] { "recall", "timeline", "stats", "profile", "topic", "commitment" };

        var plan = replanner.NextPlan("q", hint: null, attemptedSkills: all);

        Assert.NotNull(plan);
        Assert.Contains(plan!.Steps, s => s.Name == "tools");
    }
}
