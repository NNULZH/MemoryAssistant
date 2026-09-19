using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Conversation;
using MemoryAssistant.Core.Agent.Planner;
using MemoryAssistant.Core.Agent.Skills;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.RAG;

namespace MemoryAssistant.Tests;

/// <summary>
/// P17 多轮上下文单测：追问解析（他→实体 / 第N条展开 / 还有吗延续）+
/// ConversationalAgent 连续两轮不从头（零 LLM / 零真实数据）。
/// </summary>
public sealed class MultiTurnTests
{
    private static Evidence E(int index, string session, string content)
        => new() { Index = index, SessionId = session, SessionDisplayName = session, Content = content, Source = "微信原文" };

    private static ConversationSession SessionWithTurn(PlannerHint? hint = null)
    {
        var session = new ConversationSession();
        session.RecordTurn(
            "我和小明聊过工作吗？",
            "我和小明聊过工作吗？",
            "聊过：他说了秋招安排。[1] 小明：秋招有消息了吗",
            [E(1, "小明", "秋招有消息了吗")],
            hint: hint);
        return session;
    }

    [Fact]
    public void Resolver_Pronoun_MapsToLastPerson()
    {
        var session = SessionWithTurn(new PlannerHint("recall", "小明", null));
        var r = FollowUpResolver.Resolve("他后来还说了什么？", session);

        Assert.True(r.Changed);
        Assert.Contains("小明", r.Query);
        Assert.Equal("小明", r.Hint?.Entity);
        Assert.Contains("小明", r.Note);
    }

    [Fact]
    public void Resolver_ExpandNth_CitesLastEvidence()
    {
        var session = SessionWithTurn();
        session.RecordTurn("还有秋招的安排呢？", "还有秋招的安排呢？", "答复B", [E(1, "小明", "秋招A"), E(2, "老王", "下周答辩")]);

        var r = FollowUpResolver.Resolve("把第2条展开讲讲", session);

        Assert.True(r.Changed);
        // 展开不需要重新检索：直接把上一轮那条证据交给作答器（Query 保持原话，便于作答器理解上文）
        Assert.NotNull(r.DirectEvidence);
        Assert.Equal(2, r.DirectEvidence!.Index);
        Assert.Equal("老王", r.DirectEvidence.SessionDisplayName);
        Assert.Contains("下周答辩", r.DirectEvidence.Content);
        Assert.Contains("第 2 条", r.Note);
    }

    [Fact]
    public void Resolver_More_PrependsFocus()
    {
        var session = SessionWithTurn(new PlannerHint("recall", "小明", null));
        var r = FollowUpResolver.Resolve("还有更早的吗？", session);

        Assert.True(r.Changed);
        Assert.Contains("继续查", r.Query);
        Assert.Contains("小明", r.Query); // focus = 上一轮提取的人/焦点
    }

    [Fact]
    public void Resolver_PlainQuery_Unchanged()
    {
        var session = SessionWithTurn();
        var r = FollowUpResolver.Resolve("我最近和谁聊过秋招？", session);
        Assert.False(r.Changed);
        Assert.Equal("我最近和谁聊过秋招？", r.Query);
    }

    [Fact]
    public async Task Conversational_TwoTurns_ExpandsSecondTurn()
    {
        // turn1 命中"秋招"素材 → [1]；turn2 追问第1条 → 改写到"老王/下周答辩"素材
        var backend = new ScriptedBackend
        {
            Queries =
            [
                new("秋招", [new RetrievedChunk { SessionId = "小明", SessionName = "小明", Date = "2026-08-01", Text = "秋招有消息了吗", MsgCount = 1 }]),
                new("老王", [new RetrievedChunk { SessionId = "老王", SessionName = "老王", Date = "2026-08-02", Text = "下周要答辩", MsgCount = 1 }]),
            ],
        };
        var opts = new AgentOptions { MaxToolCalls = 10, MaxReplanRounds = 3, EnableReplanning = true, MaxTaskSeconds = 0 };
        var planner = new Planner(SkillCatalog.Default(), opts, chat: null);
        var skills = SkillRegistry.BuildDefault(backend);
        var agent = new ConversationalAgent(planner, skills, opts);

        var turn1 = await agent.RunAsync("我和小明聊过秋招吗？");
        Assert.True(turn1.CompletedNormally);
        Assert.Contains("秋招", turn1.Answer);
        Assert.Single(agent.Session.Turns);

        var turn2 = await agent.RunAsync("展开第1条");
        Assert.True(turn2.CompletedNormally);
        Assert.Equal(2, agent.Session.Turns.Count);
        Assert.NotNull(agent.Session.Turns[^1].Note);
        Assert.Contains("第 1 条", agent.Session.Turns[^1].Note);
    }

    /// <summary>
    /// 回归：追问里的代词不能当"实体"。
    /// 旧行为：正则会把"那个人"抓成实体并写进 LastPerson，于是下一轮连"他"都指不到人，
    /// 模型只能瞎找（实测表现：跑去翻一堆无关的群聊）。
    /// </summary>
    [Fact]
    public void RecordTurn_PronounFollowUp_KeepsPreviousPerson()
    {
        var session = new ConversationSession();
        session.RecordTurn("帮我整理我和张晓明的聊天记录", "…", "…", []);
        Assert.Equal("张晓明", session.LastPerson);

        // 追问带代词："那个人"必须被识别为指代而不是新实体
        Assert.Null(ConversationSession.ExtractPerson("继续找那个人的最新记录并回一句"));
        Assert.Null(ConversationSession.ExtractPerson("再找找他的最新消息"));
        session.RecordTurn("继续找那个人的最新记录并回一句", "…", "…", []);

        Assert.Equal("张晓明", session.LastPerson);   // 没被"那个人"冲掉
    }

    [Theory]
    [InlineData("帮我看看我和张晓明聊了什么", "张晓明")]
    [InlineData("我和紫薯马国敬9月11号聊了什么", "紫薯马国敬")]
    [InlineData("找找那个人的最新记录", null)]
    [InlineData("问他最近怎么样", null)]
    public void ExtractPerson_ResolvesNamesAndRejectsPronouns(string query, string? expected)
        => Assert.Equal(expected, ConversationSession.ExtractPerson(query));

    /// <summary>按查询关键词命中返回不同 chunk 的假底座。</summary>
    private sealed class ScriptedBackend : IMemoryBackend
    {
        public required IReadOnlyList<(string Contains, IReadOnlyList<RetrievedChunk> Chunks)> Queries { get; init; }

        public Task<IReadOnlyList<RetrievedChunk>> SemanticSearchAsync(string query, int topK, CancellationToken ct)
        {
            var hit = Queries.FirstOrDefault(q => query.Contains(q.Contains, StringComparison.Ordinal));
            return Task.FromResult(hit.Chunks ?? []);
        }

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
}
