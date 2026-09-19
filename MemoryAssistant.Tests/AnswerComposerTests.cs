using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Answer;
using MemoryAssistant.Core.Agent.Planner;
using MemoryAssistant.Core.Agent.Runtime;
using MemoryAssistant.Core.Agent.Skills;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.RAG;

namespace MemoryAssistant.Tests;

/// <summary>
/// 作答环节单测（"回答得像人话"是产品底线）：
/// 1) 有材料时由模型组织语言，材料不足/失败时不硬凑、退回确定性文案；
/// 2) 打招呼这类不需要查记录的问题也必须由模型作答，不能回一句写死的模板；
/// 3) 确定性兜底本身要按会话归类、可读，不能把原始素材原样倾倒。
/// 全部零 LLM / 零数据（ScriptedChatClient + FakeBackend）。
/// </summary>
public sealed class AnswerComposerTests
{
    // ---------- 测试替身 ----------

    private sealed class FakeBackend : IMemoryBackend
    {
        public IReadOnlyList<RetrievedChunk> Chunks { get; init; } = [];
        public Task<IReadOnlyList<RetrievedChunk>> SemanticSearchAsync(string q, int topK, CancellationToken ct)
            => Task.FromResult(Chunks);
        public Task<IReadOnlyList<Evidence>> ReadDayMessagesAsync(string s, string d, int l, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<IReadOnlyList<Evidence>> KeywordSearchAsync(string k, string? s, int l, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<string> GetStatsTextAsync(int l, int s, CancellationToken ct) => Task.FromResult("");
        public Task<IReadOnlyList<DayActivity>> GetRecentDaysAsync(int c, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DayActivity>>([]);
        public Task<IReadOnlyList<Evidence>> GetDaySnippetsAsync(string d, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<IReadOnlyList<Evidence>> ScanCommitmentsAsync(int l, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<string> GetTopicsTextAsync(int d, CancellationToken ct) => Task.FromResult("");
        public Task<string> GetProfilesTextAsync(int t, CancellationToken ct) => Task.FromResult("");
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public string ModelName => "boom";
        public Task<ChatResult> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolSchema>? tools = null, CancellationToken ct = default)
            => throw new InvalidOperationException("模型服务不可用");
    }

    private static Evidence Ev(int index, string session, string content, string source = "微信原文")
        => new()
        {
            Index = index,
            SessionId = "s1",
            SessionDisplayName = session,
            SenderName = "廖贤",
            CreateTime = 1754496000,
            Content = content,
            Source = source,
        };

    private static AgentOptions Opts() => new() { MaxToolCalls = 10, MaxTaskSeconds = 0 };

    private static async Task<AgentResult> RunAsync(
        string query, IMemoryBackend backend, IAnswerComposer? composer)
    {
        var opts = Opts();
        var planner = new Planner(SkillCatalog.Default(), opts, chat: null);
        var skills = SkillRegistry.BuildDefault(backend);
        var orchestrator = new AgentOrchestrator(planner, skills, opts, composer: composer);
        var task = new AgentTask(query);
        var rt = new TaskRuntime(opts);
        var done = await rt.RunAsync(task, orchestrator, CancellationToken.None);
        return done.Result!;
    }

    // ---------- 作答器本身 ----------

    [Fact]
    public async Task Composer_SendsNumberedEvidence_AndReturnsModelText()
    {
        IReadOnlyList<ChatMessage>? captured = null;
        var chat = new ScriptedChatClient([new ChatResult { Content = "你和张晓明聊得最多，主要是约饭 [1]。" }])
        {
            CallsHooks = msgs => captured = msgs,
        };
        var composer = new LlmAnswerComposer(chat);

        var text = await composer.ComposeAsync(new AnswerRequest
        {
            UserQuery = "我最近和谁聊得最多？",
            Evidence = [Ev(1, "张晓明", "周五一起吃饭吗")],
        }, CancellationToken.None);

        Assert.Equal("你和张晓明聊得最多，主要是约饭 [1]。", text);
        Assert.NotNull(captured);
        var user = captured!.Single(m => m.Role == "user").Content!;
        Assert.Contains("[1]", user);                  // 编号要带进去，答案才能引用
        Assert.Contains("张晓明", user);
        Assert.Contains("周五一起吃饭吗", user);
    }

    [Fact]
    public async Task Composer_SeparatesOriginalFromRagChunk()
    {
        IReadOnlyList<ChatMessage>? captured = null;
        var chat = new ScriptedChatClient([new ChatResult { Content = "ok" }]) { CallsHooks = m => captured = m };
        var composer = new LlmAnswerComposer(chat);

        await composer.ComposeAsync(new AnswerRequest
        {
            UserQuery = "q",
            Evidence = [Ev(1, "群A", "原文一条"), Ev(2, "群A", "多条消息拼成的片段", "rag")],
        }, CancellationToken.None);

        var user = captured!.Single(m => m.Role == "user").Content!;
        Assert.Contains("【聊天原文", user);
        Assert.Contains("【检索片段", user);
    }

    [Fact]
    public async Task Composer_NoMaterial_AndNotDirectReply_ReturnsNull_WithoutCallingModel()
    {
        var chat = new ScriptedChatClient([new ChatResult { Content = "不该被调用" }]);
        var composer = new LlmAnswerComposer(chat);

        var text = await composer.ComposeAsync(new AnswerRequest { UserQuery = "q" }, CancellationToken.None);

        Assert.Null(text);
        Assert.Empty(chat.Calls);
    }

    [Fact]
    public async Task Composer_Greeting_IsAnsweredByModel_NotByTemplate()
    {
        var chat = new ScriptedChatClient([new ChatResult { Content = "你好，我是你的本地记忆助手，可以帮你回忆聊天记录。" }]);
        var composer = new LlmAnswerComposer(chat);

        var text = await composer.ComposeAsync(
            new AnswerRequest { UserQuery = "你好", DirectReply = true }, CancellationToken.None);

        Assert.NotNull(text);
        Assert.Contains("你好", text);
        Assert.Single(chat.Calls);
    }

    [Fact]
    public async Task Composer_ModelFailure_ReturnsNull_SoCallerCanFallBack()
    {
        var composer = new LlmAnswerComposer(new ThrowingChatClient());
        var text = await composer.ComposeAsync(
            new AnswerRequest { UserQuery = "q", Evidence = [Ev(1, "张晓明", "约饭")] }, CancellationToken.None);
        Assert.Null(text);
    }

    // ---------- 与 Orchestrator 的配合 ----------

    [Fact]
    public async Task Orchestrator_UsesComposedAnswer()
    {
        var backend = new FakeBackend { Chunks = [Chunk("s1", "张晓明说周五一起吃饭")] };
        var composer = new LlmAnswerComposer(new ScriptedChatClient([new ChatResult { Content = "你最近常和张晓明约饭 [1]。" }]));

        var r = await RunAsync("我和张晓明聊过什么？", backend, composer);

        Assert.Equal("你最近常和张晓明约饭 [1]。", r.Answer);
    }

    [Fact]
    public async Task Orchestrator_ComposerFails_FallsBackToDeterministicDigest()
    {
        var backend = new FakeBackend { Chunks = [Chunk("s1", "张晓明说周五一起吃饭")] };

        var r = await RunAsync("我和张晓明聊过什么？", backend, new LlmAnswerComposer(new ThrowingChatClient()));

        Assert.Contains("我在你的聊天记录里找到这些相关内容", r.Answer);   // 兜底文案要说明这是原始摘录
        Assert.Contains("[1]", r.Answer);
        Assert.DoesNotContain("证据", r.Answer);                        // 不再输出"证据"这类机器话术
    }

    [Fact]
    public async Task Orchestrator_NoComposer_DigestIsGroupedBySession()
    {
        var backend = new FakeBackend
        {
            Chunks = [Chunk("s1", "张晓明说周五一起吃饭"), Chunk("s2", "张三问秋招进度")],
        };

        var r = await RunAsync("我和他们聊过什么？", backend, composer: null);

        Assert.Contains("张晓明", r.Answer);
        Assert.Contains("张三", r.Answer);
        Assert.Contains("·", r.Answer);            // 按会话分组的条目
    }

    [Fact]
    public async Task Orchestrator_Greeting_MarkedAsDirectReply()
    {
        var chat = new ScriptedChatClient([new ChatResult { Content = "你好呀，想问点什么都行。" }]);
        var r = await RunAsync("你好", new FakeBackend(), new LlmAnswerComposer(chat));

        Assert.Equal("你好呀，想问点什么都行。", r.Answer);
        Assert.Empty(r.Evidence);                  // 闲聊不该去翻记录
    }

    [Fact]
    public async Task Composer_IncludesConversationHistory_SoFollowUpsMakeSense()
    {
        IReadOnlyList<ChatMessage>? captured = null;
        var chat = new ScriptedChatClient([new ChatResult { Content = "他后来又提了一次 [1]。" }]) { CallsHooks = m => captured = m };
        var composer = new LlmAnswerComposer(chat);

        await composer.ComposeAsync(new AnswerRequest
        {
            UserQuery = "他后来还说了什么？",
            Evidence = [Ev(1, "张晓明", "周五一起吃饭")],
            History = [new AnswerTurn("我和张晓明聊过什么？", "你们主要聊了约饭和游戏 [1]")],
            LastPerson = "张晓明",
            Focus = "张晓明",
        }, CancellationToken.None);

        var user = captured!.Single(m => m.Role == "user").Content!;
        Assert.Contains("【对话上文", user);
        Assert.Contains("我和张晓明聊过什么？", user);
        Assert.Contains("你们主要聊了约饭和游戏", user);
        Assert.Contains("最近谈到的人「张晓明」", user);
        Assert.Contains("【本轮用户说】他后来还说了什么？", user);
    }

    [Fact]
    public async Task FollowUp_ExpandNthEvidence_ReusesLastTurn_WithoutSearchingAgain()
    {
        var backend = new CountingBackend { Chunks = [Chunk("s1", "周五一起吃饭吗")] };
        // 第 1 轮：正常回忆；第 2 轮：展开，应由作答器直接讲清，不再检索
        var chat = new ScriptedChatClient([
            new ChatResult { Content = "你们主要聊了约饭 [1]。" },
            new ChatResult { Content = "那条是她周五问你要不要一起吃饭。" },
        ]);
        var agent = NewAgent(backend, new LlmAnswerComposer(chat));

        var t1 = await agent.RunAsync("我和张晓明聊过什么？");
        Assert.Equal("你们主要聊了约饭 [1]。", t1.Answer);

        var t2 = await agent.RunAsync("展开第1条");

        Assert.Equal("那条是她周五问你要不要一起吃饭。", t2.Answer);
        Assert.Equal(1, backend.SemanticCalls);          // 第二轮没有再去检索
        Assert.Single(t2.Evidence);                      // 证据仍是上一条，编号可追溯
        Assert.Equal(1, t2.Evidence[0].Index);
        Assert.Contains("复用上一轮证据", t2.TaskTrace!.ToText());
    }

    [Fact]
    public async Task FollowUp_ExpandNthEvidence_WithoutComposer_StillShowsOriginalText()
    {
        var backend = new CountingBackend { Chunks = [Chunk("s1", "周五一起吃饭吗")] };
        var agent = NewAgent(backend, composer: null);

        await agent.RunAsync("我和张晓明聊过什么？");
        var missing = await agent.RunAsync("展开第 2 条");   // 上一轮只有 1 条 → 不当作展开，正常走检索
        var hit = await agent.RunAsync("展开第1条");        // 命中 → 直接复用上一轮证据

        Assert.Single(missing.Evidence);
        Assert.Equal(2, backend.SemanticCalls);             // 展开那轮没有触发新的检索
        Assert.Contains("周五一起吃饭吗", hit.Answer);       // 没有作答器时，也要把这条原文完整给出来
        Assert.Equal(1, hit.Evidence[0].Index);
    }

    private static MemoryAssistant.Core.Agent.Conversation.ConversationalAgent NewAgent(
        IMemoryBackend backend, IAnswerComposer? composer)
    {
        var opts = Opts();
        var planner = new Planner(SkillCatalog.Default(), opts, chat: null);
        var skills = SkillRegistry.BuildDefault(backend);
        return new MemoryAssistant.Core.Agent.Conversation.ConversationalAgent(
            planner, skills, opts, composer: composer);
    }

    private sealed class CountingBackend : IMemoryBackend
    {
        public IReadOnlyList<RetrievedChunk> Chunks { get; init; } = [];
        public int SemanticCalls { get; private set; }
        public Task<IReadOnlyList<RetrievedChunk>> SemanticSearchAsync(string q, int topK, CancellationToken ct)
        {
            SemanticCalls++;
            return Task.FromResult(Chunks);
        }
        public Task<IReadOnlyList<Evidence>> ReadDayMessagesAsync(string s, string d, int l, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<IReadOnlyList<Evidence>> KeywordSearchAsync(string k, string? s, int l, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<string> GetStatsTextAsync(int l, int s, CancellationToken ct) => Task.FromResult("");
        public Task<IReadOnlyList<DayActivity>> GetRecentDaysAsync(int c, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DayActivity>>([]);
        public Task<IReadOnlyList<Evidence>> GetDaySnippetsAsync(string d, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<IReadOnlyList<Evidence>> ScanCommitmentsAsync(int l, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<string> GetTopicsTextAsync(int d, CancellationToken ct) => Task.FromResult("");
        public Task<string> GetProfilesTextAsync(int t, CancellationToken ct) => Task.FromResult("");
    }

    // ---------- 规划：强关键词要能盖过"兜底意图" ----------

    [Fact]
    public void RulePlanner_RecallHint_DoesNotSwallowStatsQuestion()
    {
        var plan = RulePlanner.RulePlan("我最近和谁聊得最多？", new PlannerHint("recall", null, null));
        Assert.Equal("stats", plan.Steps[0].Name);
    }

    [Fact]
    public void RulePlanner_RecallHint_StillRecallsPlainQuestions()
    {
        var plan = RulePlanner.RulePlan("我和小明聊过什么？", new PlannerHint("recall", null, null));
        Assert.Equal("recall", plan.Steps[0].Name);
    }

    [Fact]
    public void RulePlanner_ShortEmotionalMessage_IsChitchat()
    {
        var plan = RulePlanner.RulePlan("今天好累", hint: null);
        Assert.Single(plan.Steps);
        Assert.Equal(PlanStepKind.Finish, plan.Steps[0].Kind);
    }

    [Fact]
    public void RulePlanner_EmotionalWordInsideRealQuestion_IsNotChitchat()
    {
        // 出现"聊/查"这类动作词 → 是内容类提问，不能当寒暄
        var plan = RulePlanner.RulePlan("我最近聊得好累，都跟谁聊的？", hint: new PlannerHint("recall", null, null));
        Assert.NotEqual(PlanStepKind.Finish, plan.Steps[0].Kind);
    }

    private static RetrievedChunk Chunk(string sid, string text)
        => new() { SessionId = sid, SessionName = sid, Date = "2026-08-01", Text = text, MsgCount = 1 };
}
