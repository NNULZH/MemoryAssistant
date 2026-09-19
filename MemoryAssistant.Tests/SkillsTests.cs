using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Planner;
using MemoryAssistant.Core.Agent.Runtime;
using MemoryAssistant.Core.Agent.Skills;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.RAG;

namespace MemoryAssistant.Tests;

/// <summary>
/// P13 Skill 单测：FakeBackend（零 LLM / 零真实数据）验证 7 个 Skill 的
/// 输入→执行→Observation/Evidence 契约 + SkillPlanExecutor 按计划执行。
/// </summary>
public sealed class SkillsTests
{
    private sealed class FakeBackend : IMemoryBackend
    {
        public IReadOnlyList<RetrievedChunk> Chunks { get; init; } = [];
        public IReadOnlyList<Evidence> DayMessages { get; init; } = [];
        public IReadOnlyList<Evidence> SearchHits { get; init; } = [];
        public string StatsText { get; init; } = "";
        public IReadOnlyList<DayActivity> Days { get; init; } = [];
        public IReadOnlyList<Evidence> Snippets { get; init; } = [];
        public IReadOnlyList<Evidence> Commitments { get; init; } = [];
        public string TopicsText { get; init; } = "";
        public string ProfilesText { get; init; } = "";
        public int SemanticCalls { get; private set; }

        public Task<IReadOnlyList<RetrievedChunk>> SemanticSearchAsync(string query, int topK, CancellationToken ct)
        { SemanticCalls++; return Task.FromResult(Chunks); }

        public Task<IReadOnlyList<Evidence>> ReadDayMessagesAsync(string sessionId, string date, int limit, CancellationToken ct)
            => Task.FromResult(DayMessages);

        public Task<IReadOnlyList<Evidence>> KeywordSearchAsync(string keyword, string? sessionId, int limit, CancellationToken ct)
            => Task.FromResult(SearchHits);

        public Task<string> GetStatsTextAsync(int limit, int sessionLimit, CancellationToken ct) => Task.FromResult(StatsText);

        public Task<IReadOnlyList<DayActivity>> GetRecentDaysAsync(int count, CancellationToken ct) => Task.FromResult(Days);

        public Task<IReadOnlyList<Evidence>> GetDaySnippetsAsync(string date, CancellationToken ct) => Task.FromResult(Snippets);

        public Task<IReadOnlyList<Evidence>> ScanCommitmentsAsync(int limit, CancellationToken ct) => Task.FromResult(Commitments);

        public Task<string> GetTopicsTextAsync(int recentDays, CancellationToken ct) => Task.FromResult(TopicsText);

        public Task<string> GetProfilesTextAsync(int top, CancellationToken ct) => Task.FromResult(ProfilesText);
    }

    private static FakeBackend SampleBackend() => new()
    {
        Chunks =
        [
            new RetrievedChunk { SessionId = "s1", SessionName = "同学群", Date = "2026-08-01", Text = "秋招有消息了吗", MsgCount = 3 },
            new RetrievedChunk { SessionId = "s2", SessionName = "老王", Date = "2026-08-02", Text = "我下周答辩", MsgCount = 2 },
        ],
        DayMessages = [new Evidence { SessionId = "s1", SessionDisplayName = "同学群", CreateTime = 1754000000, SenderName = "A", Content = "秋招有消息了吗", Source = "微信原文" }],
        SearchHits = [new Evidence { SessionId = "s1", Content = "秋招", Source = "微信原文" }],
        StatsText = "活跃会话 2 个，消息 300 条。高频会话：同学群(180)、老王(120)。",
        Days = [new DayActivity("2026-09-08", 120), new DayActivity("2026-09-07", 80)],
        Snippets = [new Evidence { SessionId = "s1", SessionDisplayName = "同学群", Content = "今天聊了秋招", Source = "时间线" }],
        Commitments = [new Evidence { SessionId = "s1", SessionDisplayName = "同学群", CreateTime = 1754000000, Content = "我下周答辩完交报告", Source = "承诺" }],
        TopicsText = "近30天话题：关键词：实习工作(12)、共青团资讯(5)。",
        ProfilesText = "画像：同学群(180条/20天)。",
    };

    private static SkillRegistry Registry(FakeBackend backend) => SkillRegistry.BuildDefault(backend);

    // ---------- 各 Skill 契约 ----------

    [Fact]
    public async Task RecallSkill_ReturnsEvidence_FromChunksAndRead()
    {
        var backend = SampleBackend();
        var skill = new BuiltInSkills.RecallSkill(backend);
        var r = await skill.ExecuteAsync(new SkillRequest { Query = "秋招" }, CancellationToken.None);

        Assert.True(r.Success);
        Assert.True(r.Sufficient);
        Assert.NotEmpty(r.Evidence);
        Assert.Contains(r.Evidence, e => e.Source == "rag");
        Assert.Contains(r.Evidence, e => e.Source == "微信原文");
        Assert.Contains("召回", r.Summary);
    }

    [Fact]
    public async Task StatsSkill_ReturnsDraftText()
    {
        var r = await new BuiltInSkills.StatsSkill(SampleBackend()).ExecuteAsync(
            new SkillRequest { Query = "我最近聊天量" }, CancellationToken.None);
        Assert.True(r.Sufficient);
        Assert.False(string.IsNullOrWhiteSpace(r.Draft));
        Assert.Contains("同学群", r.Draft!);
    }

    [Fact]
    public async Task TimelineSkill_ReturnsSnippets()
    {
        var r = await new BuiltInSkills.TimelineSkill(SampleBackend()).ExecuteAsync(
            new SkillRequest { Query = "上周" }, CancellationToken.None);
        Assert.True(r.Sufficient);
        Assert.NotEmpty(r.Evidence);
        Assert.Contains("活跃概况", r.Summary);
    }

    [Fact]
    public async Task CommitmentSkill_ReturnsEvidence_AndSufficesEvenIfEmpty()
    {
        var r = await new BuiltInSkills.CommitmentSkill(SampleBackend()).ExecuteAsync(
            new SkillRequest { Query = "我还欠什么" }, CancellationToken.None);
        Assert.True(r.Success);
        Assert.True(r.Sufficient); // 扫描本身成功即可答
        Assert.Single(r.Evidence);

        var empty = await new BuiltInSkills.CommitmentSkill(new FakeBackend()).ExecuteAsync(
            new SkillRequest { Query = "x" }, CancellationToken.None);
        Assert.True(empty.Success);
        Assert.True(empty.Sufficient);
        Assert.Empty(empty.Evidence);
    }

    [Fact]
    public async Task TopicSkill_AndProfileSkill_ReturnText()
    {
        var topic = await new BuiltInSkills.TopicSkill(SampleBackend()).ExecuteAsync(
            new SkillRequest { Query = "最近聊什么" }, CancellationToken.None);
        Assert.True(topic.Sufficient);
        Assert.Contains("实习工作", topic.Draft);

        var profile = await new BuiltInSkills.ProfileSkill(SampleBackend()).ExecuteAsync(
            new SkillRequest { Query = "同学群画像" }, CancellationToken.None);
        Assert.True(profile.Sufficient);
        Assert.Contains("同学群", profile.Draft);
    }

    [Fact]
    public async Task ChitchatSkill_NoBackend_NoEvidence()
    {
        var r = await new BuiltInSkills.ChitchatSkill().ExecuteAsync(
            new SkillRequest { Query = "你好" }, CancellationToken.None);
        Assert.True(r.Sufficient);
        Assert.Empty(r.Evidence);
    }

    [Fact]
    public void Registry_ExposesAllBuiltinSkills()
    {
        var reg = Registry(SampleBackend());
        Assert.Equal(8, reg.All.Count);
        foreach (var name in new[] { "recall", "stats", "timeline", "commitment", "topic", "profile", "wechat", "chitchat" })
            Assert.True(reg.TryGet(name, out _), name);
    }

    // ---------- SkillPlanExecutor：按 AgentPlan 执行 ----------

    private static AgentOptions EcoOpts() => new() { MaxToolCalls = 20, MaxTaskSeconds = 0 };

    [Fact]
    public async Task Executor_RunsRecallPlan_CollectsStepsAndEvidence()
    {
        var backend = SampleBackend();
        var reg = Registry(backend);
        var state = new TaskState("我和同学聊过秋招吗？");
        var plan = RulePlanner.RulePlan("我和同学聊过秋招吗？", hint: null);
        var budget = new AgentBudget(EcoOpts());

        var result = await new SkillPlanExecutor(reg).ExecuteAsync(plan, budget, state);

        Assert.True(result.RanToCompletion);
        Assert.True(result.Sufficient);
        Assert.NotEmpty(result.Evidence);
        Assert.Equal(1, result.Evidence[0].Index);
        Assert.Single(state.Steps);
        Assert.Equal(TaskStepStatus.Completed, state.Steps[0].Status);
        Assert.Single(state.Observations);
        Assert.NotEmpty(state.Evidence);
        Assert.Equal(1, backend.SemanticCalls);
        Assert.NotEmpty(result.StepSummaries);
    }

    [Fact]
    public async Task Executor_ChitchatPlan_NoToolConsumed()
    {
        var reg = Registry(SampleBackend());
        var plan = RulePlanner.RulePlan("你好", hint: null);
        var budget = new AgentBudget(EcoOpts());

        var result = await new SkillPlanExecutor(reg).ExecuteAsync(plan, budget, null);
        Assert.True(result.RanToCompletion);
        Assert.True(result.Sufficient);
        Assert.Equal(0, budget.ToolCallCount);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public async Task Executor_BudgetExhausted_StopsHonestly()
    {
        var reg = Registry(SampleBackend());
        var plan = RulePlanner.RulePlan("我最近是不是经常半夜和人聊天？", hint: null); // stats+timeline 两步骤
        var budget = new AgentBudget(new AgentOptions { MaxToolCalls = 1, MaxTaskSeconds = 0 });
        var state = new TaskState("q");

        var result = await new SkillPlanExecutor(reg).ExecuteAsync(plan, budget, state);

        Assert.False(result.RanToCompletion);
        Assert.NotNull(result.TerminationReason);
        Assert.Contains("budget", result.TerminationReason);
        Assert.Equal(1, budget.ToolCallCount);
        Assert.Equal("q", state.UserQuery);
    }

    [Fact]
    public async Task Executor_ReindexesEvidenceSequentially()
    {
        var backend = new FakeBackend
        {
            Chunks =
            [
                new RetrievedChunk { SessionId = "a", Date = "2026-01-01", Text = "1", MsgCount = 1 },
                new RetrievedChunk { SessionId = "b", Date = "2026-01-02", Text = "2", MsgCount = 1 },
            ],
        };
        var reg = Registry(backend);
        var plan = RulePlanner.RulePlan("随便", hint: null);
        var result = await new SkillPlanExecutor(reg).ExecuteAsync(plan, new AgentBudget(EcoOpts()), null);

        var indices = result.Evidence.Select(e => e.Index).ToList();
        Assert.Equal(indices, Enumerable.Range(1, indices.Count).ToList());
    }
}
