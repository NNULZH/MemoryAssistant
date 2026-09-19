using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Planner;
using MemoryAssistant.Core.Agent.Runtime;
using MemoryAssistant.Core.Agent.Skills;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.RAG;

namespace MemoryAssistant.Tests;

/// <summary>
/// P19 Task Trace 单测：Orchestrator 执行后 AgentResult.TaskTrace 记录
/// 每轮计划/步骤/判定/证据与终止原因（零 LLM / 零真实数据）。
/// </summary>
public sealed class TaskTraceTests
{
    private sealed class TraceBackend : IMemoryBackend
    {
        public IReadOnlyList<RetrievedChunk> Chunks { get; init; } = [];
        public IReadOnlyList<DayActivity> Days { get; init; } = [];
        public IReadOnlyList<Evidence> Snippets { get; init; } = [];

        public Task<IReadOnlyList<RetrievedChunk>> SemanticSearchAsync(string query, int topK, CancellationToken ct) => Task.FromResult(Chunks);
        public Task<IReadOnlyList<Evidence>> ReadDayMessagesAsync(string sessionId, string date, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<IReadOnlyList<Evidence>> KeywordSearchAsync(string keyword, string? sessionId, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<string> GetStatsTextAsync(int limit, int sessionLimit, CancellationToken ct) => Task.FromResult("");
        public Task<IReadOnlyList<DayActivity>> GetRecentDaysAsync(int count, CancellationToken ct) => Task.FromResult(Days);
        public Task<IReadOnlyList<Evidence>> GetDaySnippetsAsync(string date, CancellationToken ct) => Task.FromResult(Snippets);
        public Task<IReadOnlyList<Evidence>> ScanCommitmentsAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<string> GetTopicsTextAsync(int recentDays, CancellationToken ct) => Task.FromResult("");
        public Task<string> GetProfilesTextAsync(int top, CancellationToken ct) => Task.FromResult("");
    }

    private static AgentOptions Opts(int maxTools = 10, int maxReplan = 3, bool replan = true) => new()
    {
        MaxToolCalls = maxTools, MaxReplanRounds = maxReplan, EnableReplanning = replan, MaxTaskSeconds = 0,
    };

    private static async Task<AgentResult> RunAsync(string query, TraceBackend backend, AgentOptions opts)
    {
        var planner = new Planner(SkillCatalog.Default(), opts, chat: null);
        var skills = SkillRegistry.BuildDefault(backend);
        var task = new AgentTask(query);
        var rt = new TaskRuntime(opts);
        var done = await rt.RunAsync(task, new AgentOrchestrator(planner, skills, opts), CancellationToken.None);
        return done.Result!;
    }

    [Fact]
    public async Task SuccessfulRun_ProducesOneCycleTrace()
    {
        var backend = new TraceBackend
        {
            Chunks = [new RetrievedChunk { SessionId = "s1", SessionName = "小明", Date = "2026-08-01", Text = "秋招有消息了吗", MsgCount = 1 }],
        };
        var r = await RunAsync("我和小明聊过秋招吗？", backend, Opts());

        Assert.True(r.CompletedNormally);
        Assert.NotNull(r.TaskTrace);
        var trace = r.TaskTrace!;
        Assert.True(trace.Completed);
        Assert.Single(trace.Cycles);
        var cycle = trace.Cycles[0];
        Assert.Contains("skill:recall", cycle.PlannedSteps);
        var step = Assert.Single(cycle.Steps);
        Assert.Equal("recall", step.Skill);
        Assert.True(step.Success);
        Assert.NotEmpty(cycle.EvidenceGained);
        Assert.Contains("answer", cycle.Verdict);

        var text = trace.ToText();
        Assert.Contains("任务: 我和小明聊过秋招吗？", text);
        Assert.Contains("✓ recall", text);
        Assert.Contains("判定: answer", text);
    }

    [Fact]
    public async Task Replan_RecordsTwoCycles()
    {
        var backend = new TraceBackend
        {
            Days = [new DayActivity("2026-09-08", 3)],
            Snippets = [new Evidence { SessionId = "s2", SessionDisplayName = "老王", Content = "最近聊的都是实验", Source = "时间线" }],
        };
        var r = await RunAsync("我最近和朋友聊过什么吗", backend, Opts()); // 触发默认 recall → 换 timeline

        Assert.True(r.CompletedNormally);
        var trace = r.TaskTrace!;
        Assert.Equal(2, trace.Cycles.Count);
        Assert.Contains("skill:timeline", trace.Cycles[1].PlannedSteps);
        var text = trace.ToText();
        Assert.Contains("replan", text);
        Assert.Contains("✓ timeline", text);
    }

    [Fact]
    public async Task BudgetStop_TraceTerminationReason()
    {
        var backend = new TraceBackend(); // stats/timeline 均无数据；预算=1 → timeline 触发 budget 终止
        var r = await RunAsync("我最近是不是经常半夜和人聊天？", backend, Opts(maxTools: 1));

        Assert.False(r.CompletedNormally);
        var trace = r.TaskTrace!;
        Assert.False(trace.Completed);
        Assert.NotNull(trace.TerminationReason);
        Assert.Contains("budget", trace.TerminationReason);
        Assert.Contains("budget", trace.ToText());
    }
}
