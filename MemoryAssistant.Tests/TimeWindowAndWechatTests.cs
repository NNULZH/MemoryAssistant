using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Planner;
using MemoryAssistant.Core.Agent.Query;
using MemoryAssistant.Core.Agent.Skills;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.Integrations;
using MemoryAssistant.Core.RAG;

namespace MemoryAssistant.Tests;

/// <summary>V3.3 单测：时间窗检索 / 噪音过滤 / 召回按窗口过滤 / 微信窗口只读 Skill（零 LLM/零数据）。</summary>
public sealed class TimeWindowAndWechatTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-08T12:00:00+08:00");

    // ---------- 时间窗解析 ----------

    [Theory]
    [InlineData(TimeKind.LastYear, "2025-08-01", true)]
    [InlineData(TimeKind.LastYear, "2026-08-01", false)]
    [InlineData(TimeKind.Recent, "2026-09-08", true)]
    [InlineData(TimeKind.Recent, "2026-08-01", false)]
    [InlineData(TimeKind.LastMonth, "2026-08-15", true)]
    [InlineData(TimeKind.LastMonth, "2026-09-01", false)]
    public void Window_FiltersExpectedDates(TimeKind kind, string date, bool expected)
    {
        var model = new QueryModel { Raw = "x", Goal = "x", TimeKind = kind, TimePhrase = "" };
        var w = TimeWindowResolver.Resolve(model, Now);
        Assert.Equal(expected, TimeWindowResolver.ContainsDate(w, date));
    }

    [Fact]
    public void Window_RangeDays_FromPhrase()
    {
        var model = new QueryModel { Raw = "x", Goal = "x", TimeKind = TimeKind.RangeDays, TimePhrase = "最近30天" };
        var w = TimeWindowResolver.Resolve(model, Now);
        Assert.True(TimeWindowResolver.ContainsDate(w, "2026-08-20"));
        Assert.False(TimeWindowResolver.ContainsDate(w, "2026-06-01"));
    }

    [Fact]
    public void Window_None_IsUnbounded()
    {
        var w = TimeWindowResolver.Resolve(new QueryModel { Raw = "x", Goal = "x" }, Now);
        Assert.False(w.IsBounded);
        Assert.True(TimeWindowResolver.ContainsDate(w, "2020-01-01"));
    }

    // ---------- 噪音过滤 ----------

    [Theory]
    [InlineData("你已添加了一缕光、，现在可以开始聊天了。", true)]
    [InlineData("我通过了你的朋友验证请求，现在我们可以开始聊天了", true)]
    [InlineData("[表情包]", true)]
    [InlineData("[链接]", true)]
    [InlineData("好", true)]
    [InlineData("昨晚就6个小时睡眠", false)]
    [InlineData("秋招多往外面看看吧，我们学校的秋招现场都没什么好的", false)]
    public void NoiseFilter_Classifies(string content, bool expectedNoise)
    {
        Assert.Equal(expectedNoise, ContentNoiseFilter.IsNoise(content));
    }

    // ---------- 召回：时间窗 + 噪音 ----------

    private sealed class FakeBackend : IMemoryBackend
    {
        public IReadOnlyList<RetrievedChunk> Chunks { get; init; } = [];
        public Task<IReadOnlyList<RetrievedChunk>> SemanticSearchAsync(string query, int topK, CancellationToken ct) => Task.FromResult(Chunks);
        public Task<IReadOnlyList<Evidence>> ReadDayMessagesAsync(string sessionId, string date, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<IReadOnlyList<Evidence>> KeywordSearchAsync(string keyword, string? sessionId, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<string> GetStatsTextAsync(int limit, int sessionLimit, CancellationToken ct) => Task.FromResult("");
        public Task<IReadOnlyList<DayActivity>> GetRecentDaysAsync(int count, CancellationToken ct) => Task.FromResult<IReadOnlyList<DayActivity>>([]);
        public Task<IReadOnlyList<Evidence>> GetDaySnippetsAsync(string date, CancellationToken ct) => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<IReadOnlyList<Evidence>> ScanCommitmentsAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<string> GetTopicsTextAsync(int recentDays, CancellationToken ct) => Task.FromResult("");
        public Task<string> GetProfilesTextAsync(int top, CancellationToken ct) => Task.FromResult("");
    }

    [Fact]
    public async Task RecallSkill_FiltersByWindow_AndNoise()
    {
        var backend = new FakeBackend
        {
            Chunks =
            [
                new RetrievedChunk { SessionId = "s1", SessionName = "老王", Date = "2026-09-08", Text = "最近聊的都是实验安排", MsgCount = 1 },
                new RetrievedChunk { SessionId = "s2", SessionName = "旧群", Date = "2026-06-01", Text = "很早以前的消息", MsgCount = 1 },
                new RetrievedChunk { SessionId = "s3", SessionName = "某人", Date = "2026-09-07", Text = "我通过了你的朋友验证请求，现在我们可以开始聊天了", MsgCount = 1 },
            ],
        };
        var skill = new BuiltInSkills.RecallSkill(backend);
        var r = await skill.ExecuteAsync(new SkillRequest
        {
            Query = "最近聊了什么",
            Window = TimeWindowResolver.Resolve(new QueryModel { Raw = "x", Goal = "x", TimeKind = TimeKind.Recent }, Now),
        }, CancellationToken.None);

        Assert.True(r.Sufficient);
        var only = Assert.Single(r.Evidence);
        Assert.Contains("实验安排", only.Content);      // 窗口内
        Assert.DoesNotContain(r.Evidence, e => e.Content.Contains("很早以前"));  // 窗口外被过滤
        Assert.DoesNotContain(r.Evidence, e => e.Content.Contains("验证请求"));  // 噪音被过滤
    }

    // ---------- 微信窗口只读 Skill ----------

    private sealed class FakeWeChat : IWeChatWindowBridge
    {
        public bool IsAvailable => Windows.Count > 0;
        public List<WindowInfo> Windows { get; init; } = [];
        public VisibleChatRead Read { get; init; } = new() { Success = false, Error = "no" };
        public Task<IReadOnlyList<WindowInfo>> ListWeChatWindowsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WindowInfo>>(Windows);
        public Task<VisibleChatRead> ReadVisibleChatAsync(CancellationToken ct = default) => Task.FromResult(Read);
    }

    [Fact]
    public async Task WechatSkill_NoBridge_DegradesGracefully()
    {
        var r = await new WechatSkill(null).ExecuteAsync(new SkillRequest { Query = "看看微信" }, CancellationToken.None);
        Assert.True(r.Success);
        Assert.False(r.Sufficient);
        Assert.Contains("未接入", r.Summary);
    }

    [Fact]
    public async Task WechatSkill_NoWindow_ReportsHonestly()
    {
        var r = await new WechatSkill(new FakeWeChat()).ExecuteAsync(new SkillRequest { Query = "看看微信" }, CancellationToken.None);
        Assert.True(r.Success);
        Assert.False(r.Sufficient);
        Assert.Contains("未找到微信窗口", r.Summary);
    }

    [Fact]
    public async Task WechatSkill_ReadsMessages_AndFiltersNoise()
    {
        var bridge = new FakeWeChat
        {
            Windows = [new WindowInfo("微信", "WeChatMainWndForPC", 1234, true)],
            Read = new VisibleChatRead
            {
                Success = true,
                Messages = ["我通过了你的朋友验证请求，现在我们可以开始聊天了", "明天下午三点开会", "好"],
            },
        };
        var r = await new WechatSkill(bridge).ExecuteAsync(new SkillRequest { Query = "看看微信" }, CancellationToken.None);

        Assert.True(r.Sufficient);
        Assert.Single(r.Evidence);
        Assert.Contains("明天下午三点开会", r.Evidence[0].Content);
        Assert.Equal("微信窗口", r.Evidence[0].Source);
    }

    [Fact]
    public async Task Planner_MapsWechatIntent()
    {
        var planner = new Planner(SkillCatalog.Default(), new AgentOptions(), chat: null);
        var plan = await planner.PlanAsync("看看微信当前聊天", preferLlm: false);
        Assert.Contains(plan.Steps, s => s.Kind == PlanStepKind.Skill && s.Name == "wechat");
    }
}
