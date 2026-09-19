using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Query;
using MemoryAssistant.Core.Agent.Skills;
using MemoryAssistant.Core.RAG;

namespace MemoryAssistant.Tests;

/// <summary>
/// 时间范围检索单测（"翻出我和某人某天的聊天记录"这件事的基础）：
/// 1) 口语日期要认得（"9月11号"以前识别不了，于是整条链路当没看见日期）；
/// 2) 日期要变成真正的检索窗口；
/// 3) 问到具体某天时，必须**按天取记录**，不能指望语义搜索恰好把那天排进前几名。
/// </summary>
public sealed class TimeScopeTests
{
    private static int Year => DateTimeOffset.Now.Year;

    // ---------- 1) 口语日期识别 ----------

    [Theory]
    [InlineData("我和张晓明9月11号聊了什么", 9, 11)]
    [InlineData("我和张晓明9月11日聊了什么", 9, 11)]
    [InlineData("9月11号", 9, 11)]
    [InlineData("看看9-11的记录", 9, 11)]
    [InlineData("看看9/11的记录", 9, 11)]
    public void TryFind_ColloquialDates(string text, int month, int day)
    {
        Assert.True(DatePhrase.TryFind(text, out var date, out var phrase));
        Assert.Equal(month, date.Month);
        Assert.Equal(day, date.Day);
        Assert.False(string.IsNullOrWhiteSpace(phrase));
    }

    [Fact]
    public void TryFind_WithYear()
    {
        Assert.True(DatePhrase.TryFind("2025年8月16日聊的", out var date, out _));
        Assert.Equal(new DateTime(2025, 8, 16), date);
    }

    [Theory]
    [InlineData("我最近和谁聊得最多")]
    [InlineData("帮我追踪和小明的聊天")]
    [InlineData("2月30日不存在")]
    public void TryFind_NoDate_ReturnsFalse(string text)
    {
        Assert.False(DatePhrase.TryFind(text, out _, out _));
    }

    // ---------- 2) 目标抽取 → 检索窗口 ----------

    [Fact]
    public void Analyzer_RecognizesDateAndPerson()
    {
        var model = new RuleQueryAnalyzer().Analyze("我和张晓明9月11号聊了什么？");

        Assert.Equal(TimeKind.SpecificDate, model.TimeKind);
        Assert.Contains("张晓明", model.Entities);

        var window = TimeWindowResolver.Resolve(model);
        Assert.True(window.IsBounded);
        Assert.Equal([$"{Year}-09-11"], TimeWindowResolver.Days(window));
        Assert.True(TimeWindowResolver.ContainsDate(window, $"{Year}-09-11"));
        Assert.False(TimeWindowResolver.ContainsDate(window, $"{Year}-09-08"));
    }

    // ---------- 3) 按天取记录（不靠语义检索碰运气） ----------

    private sealed class DayBackend : IMemoryBackend
    {
        public IReadOnlyList<RetrievedChunk> Chunks { get; init; } = [];   // 默认：语义检索什么都找不到
        public IReadOnlyList<Evidence> DaySnippets { get; init; } = [];
        public List<string> RequestedDays { get; } = [];

        public Task<IReadOnlyList<RetrievedChunk>> SemanticSearchAsync(string q, int topK, CancellationToken ct)
            => Task.FromResult(Chunks);
        public Task<IReadOnlyList<Evidence>> ReadDayMessagesAsync(string s, string d, int l, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<IReadOnlyList<Evidence>> KeywordSearchAsync(string k, string? s, int l, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<string> GetStatsTextAsync(int l, int s, CancellationToken ct) => Task.FromResult("");
        public Task<IReadOnlyList<DayActivity>> GetRecentDaysAsync(int c, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DayActivity>>([]);
        public Task<IReadOnlyList<Evidence>> GetDaySnippetsAsync(string date, CancellationToken ct)
        {
            RequestedDays.Add(date);
            return Task.FromResult(DaySnippets);
        }
        public Task<IReadOnlyList<Evidence>> ScanCommitmentsAsync(int l, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<string> GetTopicsTextAsync(int d, CancellationToken ct) => Task.FromResult("");
        public Task<string> GetProfilesTextAsync(int t, CancellationToken ct) => Task.FromResult("");
    }

    private static Evidence Snip(string session, string sender, string content)
        => new()
        {
            SessionId = "s1",
            SessionDisplayName = session,
            SenderName = sender,
            Content = content,
            Source = "时间线",
        };

    [Fact]
    public async Task Recall_DateQuestion_ReadsThatDay_EvenWhenSemanticSearchFindsNothing()
    {
        var backend = new DayBackend
        {
            DaySnippets =
            [
                Snip("张晓明", "张晓明", "[09:17] [表情包]"),
                Snip("张晓明", "求一下通项公式", "[11:56] 晚上打游戏吗"),
                Snip("别的群", "路人甲", "[10:00] 团购通知"),
            ],
        };
        var skill = new BuiltInSkills.RecallSkill(backend);
        var offset = TimeSpan.FromHours(8);
        var window = new SearchWindow(
            new DateTimeOffset(new DateTime(Year, 9, 11, 0, 0, 0), offset).ToUnixTimeSeconds(),
            new DateTimeOffset(new DateTime(Year, 9, 12, 0, 0, 0), offset).ToUnixTimeSeconds());

        var r = await skill.ExecuteAsync(new SkillRequest
        {
            Query = "我和张晓明9月11号聊了什么？",
            Window = window,
            Hint = new MemoryAssistant.Core.Agent.Planner.PlannerHint("recall", "张晓明", null),
        }, CancellationToken.None);

        Assert.True(r.Success);
        Assert.True(r.Sufficient);
        Assert.Equal([$"{Year}-09-11"], backend.RequestedDays);          // 确实去翻了那一天
        Assert.All(r.Evidence, e => Assert.Equal($"{Year}-09-11", e.Date)); // 日期随证据带出来（防止把 9.8 说成 9.11）
        Assert.Contains(r.Evidence, e => e.Content.Contains("表情包"));    // 该人相关片段保留
        Assert.DoesNotContain(r.Evidence, e => e.Content.Contains("团购通知")); // 无关会话被过滤掉
    }

    [Fact]
    public async Task Recall_NoDateInQuestion_DoesNotReadByDay()
    {
        var backend = new DayBackend { Chunks = [new RetrievedChunk { SessionId = "s1", SessionName = "张晓明", Date = "2026-08-16", Text = "周五一起吃饭吧，顺便聊聊秋招", MsgCount = 1 }] };
        var skill = new BuiltInSkills.RecallSkill(backend);

        var r = await skill.ExecuteAsync(new SkillRequest { Query = "我和张晓明聊过什么？" }, CancellationToken.None);

        Assert.Empty(backend.RequestedDays);
        Assert.Single(r.Evidence);
    }
}
