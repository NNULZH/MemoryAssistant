using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Conversation;
using MemoryAssistant.Core.Agent.Planner;
using MemoryAssistant.Core.Agent.Runtime;
using MemoryAssistant.Core.Agent.Skills;
using MemoryAssistant.Core.Agent.Trace;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.Workflow;

namespace MemoryAssistant.Tests;

/// <summary>
/// V4.1「模型思考过程 + 自主工具调用」单测（零 LLM / 零真实数据）：
///  - AgentLoop 逐轮留存 reasoning_content 与工具轮的意图自述；
///  - 思考过程绝不回填进下一次请求的 messages（DeepSeek 会 400）；
///  - ToolCallingSkill 把思考与工具轨迹交给上层；
///  - Orchestrator 直接采用 tools 的产出（回归：修复前会被截断成 240 字"原始摘录"）；
///  - TraceBuilder / TaskTrace.ToText 展示思考与逐次工具调用。
/// </summary>
public sealed class ReasoningTraceTests
{
    private static AgentOptions Options() => new()
    {
        MaxRounds = 5, MaxToolResultChars = 4000, EnableReplanning = false, MaxTaskSeconds = 0,
    };

    private static ToolRegistry SearchTools()
    {
        var registry = new ToolRegistry();
        registry.Register(new ToolDefinition
        {
            Name = "search_messages",
            Description = "全文检索消息",
            ExecuteAsync = (_, _) => Task.FromResult(new ToolCallResult { Success = true, Output = "命中 2 条" }),
        });
        return registry;
    }

    [Fact]
    public async Task AgentLoop_KeepsReasoningAndIntentPerRound()
    {
        var client = new ScriptedChatClient([
            new ChatResult
            {
                ReasoningContent = "用户问秋招，我得先搜关键词",
                Content = "我来帮你搜一下秋招。",
                ToolCalls = [new ToolCallRequest { Id = "c1", Name = "search_messages", ArgumentsJson = """{"keyword":"秋招"}""" }],
                FinishReason = "tool_calls",
            },
            new ChatResult { ReasoningContent = "搜到 2 条，可以回答了", Content = "聊过两次。", FinishReason = "stop" },
        ]);
        var loop = new AgentLoop(client, SearchTools(), Options());

        var result = await loop.RunAsync("sys", "我和同学聊过秋招吗？");

        Assert.Equal(2, result.Rounds.Count);
        Assert.Equal("用户问秋招，我得先搜关键词", result.Rounds[0].Reasoning);
        Assert.Equal("我来帮你搜一下秋招。", result.Rounds[0].AssistantContent);
        Assert.Equal("search_messages", Assert.Single(result.Rounds[0].ToolCalls).Name);
        Assert.Equal("搜到 2 条，可以回答了", result.Rounds[1].Reasoning);
        Assert.Equal("聊过两次。", result.Rounds[1].AssistantContent);

        // 汇总：两轮思考 + 工具轮的意图自述都要在
        Assert.Contains("用户问秋招，我得先搜关键词", result.Reasoning);
        Assert.Contains("（说）我来帮你搜一下秋招。", result.Reasoning);
        Assert.Contains("搜到 2 条，可以回答了", result.Reasoning);
    }

    [Fact]
    public async Task AgentLoop_ReasoningNeverSentBackToModel()
    {
        const string secret = "这是私有思考，不能回填";
        var messagesPerRound = new List<IReadOnlyList<ChatMessage>>();
        var client = new ScriptedChatClient([
            new ChatResult
            {
                ReasoningContent = secret,
                Content = "先搜一下。",
                ToolCalls = [new ToolCallRequest { Id = "c1", Name = "search_messages" }],
                FinishReason = "tool_calls",
            },
            new ChatResult { ReasoningContent = secret, Content = "好了", FinishReason = "stop" },
        ])
        {
            CallsHooks = m => messagesPerRound.Add(m.ToList()),
        };
        var loop = new AgentLoop(client, SearchTools(), Options());

        var result = await loop.RunAsync("sys", "查");

        Assert.Contains(secret, result.Reasoning);
        Assert.Equal(2, messagesPerRound.Count);
        Assert.All(
            messagesPerRound.SelectMany(m => m),
            m => Assert.DoesNotContain(secret, m.Content ?? ""));
    }

    [Fact]
    public async Task ToolCallingSkill_ExposesReasoningAndToolTraces()
    {
        var client = new ScriptedChatClient([
            new ChatResult
            {
                ReasoningContent = "该查本地记录",
                Content = "查一下。",
                ToolCalls = [new ToolCallRequest { Id = "c1", Name = "search_messages" }],
                FinishReason = "tool_calls",
            },
            new ChatResult { Content = "晴，25 度", FinishReason = "stop" },
        ]);
        var skill = new ToolCallingSkill(client, SearchTools(), Options());

        var r = await skill.ExecuteAsync(new SkillRequest { Query = "上海天气" }, CancellationToken.None);

        Assert.True(r.Sufficient);
        Assert.Equal("晴，25 度", r.Draft);
        Assert.Contains("该查本地记录", r.Reasoning);
        Assert.Equal("search_messages", Assert.Single(r.ToolCalls).Name);
    }

    private sealed class FixedPlanner(AgentPlan plan) : IPlanner
    {
        public SkillCatalog Catalog { get; } = SkillCatalog.Default();

        public Task<AgentPlan> PlanAsync(
            string query, PlannerHint? hint = null, bool preferLlm = true, CancellationToken ct = default,
            Action<string>? onReasoningDelta = null)
            => Task.FromResult(plan);
    }

    private sealed class FakeToolsSkill(string draft) : IAgentSkill
    {
        public string Name => "tools";
        public string Description => "假工具 Skill（单测用）";

        public Task<SkillResult> ExecuteAsync(SkillRequest request, CancellationToken ct)
            => Task.FromResult(new SkillResult
            {
                Skill = Name,
                Success = true,
                Sufficient = true,
                Summary = "调用工具 1 个：search_messages",
                Draft = draft,
                Reasoning = "先搜再答",
                ToolCalls =
                [
                    new ToolCallTrace { Name = "search_messages", Success = true, Output = "命中 2 条", Round = 1 },
                ],
            });
    }

    [Fact]
    public async Task Orchestrator_UsesToolsDraftAsAnswer_WhenNoEvidence()
    {
        // 回归：修复前 tools 的 Draft 会被 CollectNotes 跳过、BuildAnswer 截断到 240 字当"原始摘录"，
        // 于是"模型调通了工具"但用户看到的是一段残缺内容。
        var longAnswer = new string('答', 400);
        var planner = new FixedPlanner(new AgentPlan
        {
            Goal = "帮我找找秋招",
            Steps = [new PlanStep { Id = "s1", Kind = PlanStepKind.Skill, Name = "tools", Reason = "多步查证" }],
            Reasoning = "需要先搜关键词再回答",
        });
        var skills = new SkillRegistry([new FakeToolsSkill(longAnswer)]);
        var opts = Options();

        var rt = new TaskRuntime(opts);
        var done = await rt.RunAsync(
            new AgentTask("帮我找找秋招"), new AgentOrchestrator(planner, skills, opts), CancellationToken.None);
        var r = done.Result!;

        Assert.True(r.CompletedNormally);
        Assert.Equal(longAnswer, r.Answer);

        var cycle = Assert.Single(r.TaskTrace!.Cycles);
        Assert.Equal("需要先搜关键词再回答", cycle.PlanReasoning);
        var step = Assert.Single(cycle.Steps);
        Assert.Equal("先搜再答", step.Reasoning);
        Assert.Equal("search_messages", Assert.Single(step.ToolCalls).Name);
    }

    [Fact]
    public async Task TaskTrace_ToText_PrintsThinkingAndToolCalls()
    {
        var planner = new FixedPlanner(new AgentPlan
        {
            Goal = "帮我找找秋招",
            Steps = [new PlanStep { Id = "s1", Kind = PlanStepKind.Skill, Name = "tools", Reason = "多步查证" }],
            Reasoning = "需要先搜关键词再回答",
        });
        var skills = new SkillRegistry([new FakeToolsSkill("找到了")]);
        var opts = Options();

        var rt = new TaskRuntime(opts);
        var done = await rt.RunAsync(
            new AgentTask("帮我找找秋招"), new AgentOrchestrator(planner, skills, opts), CancellationToken.None);

        var text = done.Result!.TaskTrace!.ToText();
        Assert.Contains("规划思考: 需要先搜关键词再回答", text);
        Assert.Contains("思考: 先搜再答", text);
        Assert.Contains("→ 调用 search_messages", text);
    }

    private sealed class CountingSkill : IAgentSkill
    {
        public int Calls;
        public string Name => "tools";
        public string Description => "计数技能（单测：重复步骤应只执行一次）";

        public Task<SkillResult> ExecuteAsync(SkillRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new SkillResult
            {
                Skill = Name, Success = true, Sufficient = true, Draft = "结论",
            });
        }
    }

    [Fact]
    public async Task Orchestrator_SkipsDuplicateSkillSteps()
    {
        // 模型偶尔排出 [tools, profile, topic, tools]：第二个 tools 只会白烧一轮预算和几十秒
        var skill = new CountingSkill();
        var planner = new FixedPlanner(new AgentPlan
        {
            Goal = "重复步骤",
            Steps =
            [
                new PlanStep { Id = "s1", Kind = PlanStepKind.Skill, Name = "tools" },
                new PlanStep { Id = "s2", Kind = PlanStepKind.Skill, Name = "tools" },
            ],
        });
        var opts = Options();
        var rt = new TaskRuntime(opts);

        var done = await rt.RunAsync(
            new AgentTask("查"), new AgentOrchestrator(planner, new SkillRegistry([skill]), opts), CancellationToken.None);

        Assert.Equal(1, skill.Calls);
        Assert.Single(done.Result!.TaskTrace!.Cycles[0].Steps);
    }

    private sealed class SlowSkill : IAgentSkill
    {
        public string Name => "tools";
        public string Description => "慢技能（单测：触发任务超时）";

        public async Task<SkillResult> ExecuteAsync(SkillRequest request, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new SkillResult { Skill = Name, Success = true, Sufficient = true, Draft = "不该走到这里" };
        }
    }

    [Fact]
    public async Task ConversationalAgent_TaskTimeout_ReportsHonestFailureInsteadOfNre()
    {
        // 回归：任务超时被取消时 task.Result 为 null，旧代码 done.Result! 直接解引用 →
        // 抛 "Object reference not set"，真正的失败原因（超时）被整个吞掉。
        var planner = new FixedPlanner(new AgentPlan
        {
            Goal = "慢慢查",
            Steps = [new PlanStep { Id = "s1", Kind = PlanStepKind.Skill, Name = "tools" }],
        });
        var opts = Options();
        opts.MaxTaskSeconds = 1;
        var agent = new ConversationalAgent(
            planner, new SkillRegistry([new SlowSkill()]), opts, enableLlmPlanning: false);

        var r = await agent.RunAsync("慢慢查一下");

        Assert.False(r.CompletedNormally);
        Assert.Contains("MaxTaskSeconds", r.EarlyStopReason);
        Assert.Contains("MaxTaskSeconds", r.Answer);
    }

    [Fact]
    public void TraceBuilder_RendersThinkingAndToolCalls()
    {
        var trace = new TaskTrace
        {
            UserQuery = "帮我找找秋招",
            Completed = true,
            Cycles =
            [
                new TraceCycle
                {
                    Number = 1,
                    Goal = "帮我找找秋招",
                    PlannedSteps = ["skill:tools"],
                    PlanReasoning = "想一下该用什么能力",
                    Steps =
                    [
                        new CycleStepTrace
                        {
                            Skill = "tools",
                            Summary = "调用工具 1 个：search_messages",
                            Reasoning = "先搜再答",
                            ToolCalls =
                            [
                                new ToolCallTrace
                                {
                                    Name = "search_messages",
                                    ArgumentsJson = """{"keyword":"秋招"}""",
                                    Success = true,
                                    Output = "命中 2 条",
                                    Round = 1,
                                },
                            ],
                        },
                    ],
                    Verdict = "answer：证据足够",
                },
            ],
        };

        var steps = TraceBuilder.Build(trace);

        Assert.Contains(steps, s => s.Category == "plan" && s.Text.Contains("思考: 想一下该用什么能力"));
        Assert.Contains(steps, s => s.Category == "think" && s.Text.Contains("思考: 先搜再答"));
        Assert.Contains(steps, s => s.Category == "tool" && s.Text.Contains("调用 search_messages"));
        Assert.Contains(steps, s => s.Category == "tool" && s.Text.Contains("命中 2 条"));
    }

    [Fact]
    public async Task AgentLoop_LeakedToolCallText_AsksForRealAnswer()
    {
        // 实测：模型偶尔不走协议，把工具调用写成半角 XML 正文（清洗后是空串）。
        // 这时必须回一句纠正让它重说，而不是把一个空回答交给用户。
        var client = new ScriptedChatClient([
            new ChatResult
            {
                Content = "<tool_calls>\n<invoke>\n<parameter name=\"keyword\">镇江</parameter>\n"
                    + "<" + "/tool_" + "calls",
                FinishReason = "stop",
            },
            new ChatResult { Content = "聊过镇江，8 月的事。", FinishReason = "stop" },
        ]);
        var loop = new AgentLoop(client, SearchTools(), Options());

        var result = await loop.RunAsync("sys", "查镇江");

        Assert.Equal("聊过镇江，8 月的事。", result.Answer);
        Assert.Equal(2, result.Rounds.Count);
        Assert.Contains("tool_calls", result.Rounds[0].AssistantContent!);   // 原始泄漏留在轨迹里可追溯
    }

    // ---- 工具产出 → 证据（答案可引用 [N]、UI 证据卡可跳转） ----

    private static ToolRegistry JsonTool(string name, string output)
    {
        var registry = new ToolRegistry();
        registry.Register(new ToolDefinition
        {
            Name = name,
            Description = "单测用：返回固定 JSON",
            ExecuteAsync = (_, _) => Task.FromResult(new ToolCallResult { Success = true, Output = output }),
        });
        return registry;
    }

    [Fact]
    public void Extract_SearchMessages_MapsFieldsAndSkipsPlaceholders()
    {
        var json = """
        [{"session_id":"s1","create_time":1788395549,"sender":"wxid_a","display_name":"紫薯","is_self":false,"local_type":1,"content":"秋招有消息了吗"},
         {"session_id":"s1","create_time":1788395550,"sender":"wxid_me","display_name":"求一下通项公式","is_self":true,"local_type":1,"content":"还在等"},
         {"session_id":"s1","create_time":1788395551,"sender":"wxid_b","display_name":"B","is_self":false,"local_type":47,"content":"[表情包]"}]
        """;

        var list = ToolEvidenceExtractor.Extract("search_messages", "{}", json);

        Assert.Equal(2, list.Count);                                  // 表情包占位被跳过
        Assert.Equal("s1", list[0].SessionId);
        Assert.Equal(1788395549, list[0].CreateTime);
        Assert.Equal("紫薯", list[0].SenderName);
        Assert.Equal("秋招有消息了吗", list[0].Content);
        Assert.Equal("chat", list[0].Source);
        Assert.Equal("我", list[1].SenderName);                       // is_self → 我
    }

    [Fact]
    public void Extract_ReadMessages_TakesSessionIdFromArgs()
    {
        // read_messages 的返回里没有 session_id（由请求参数限定）
        var json = """[{"create_time":1789182161,"sender":"wxid_z","display_name":"二小","is_self":false,"local_type":1,"content":"你注意休息"}]""";

        var list = ToolEvidenceExtractor.Extract("read_messages", """{"session_id":"wxid_z"}""", json);

        var ev = Assert.Single(list);
        Assert.Equal("wxid_z", ev.SessionId);
        Assert.Equal("你注意休息", ev.Content);
    }

    [Fact]
    public void Extract_SessionNameWinsOverSenderName()
    {
        // 私聊：session_name 是**对方**；display_name 是发言者——你自己发的那条会是你自己的名字。
        // 拿 display_name 当会话名就会张冠李戴（模型甚至会以为"这个会话只有我一个人"）。
        var json = """
        [{"create_time":1,"sender":"me","display_name":"求一下通项公式","is_self":true,"content":"在的","session_name":"张晓明"},
         {"create_time":2,"sender":"wxid_r","display_name":"张晓明","is_self":false,"content":"好的","session_name":"张晓明"}]
        """;

        var list = ToolEvidenceExtractor.Extract("read_messages", "{}", json);

        Assert.All(list, e => Assert.Equal("张晓明", e.SessionDisplayName));
        Assert.Equal("我", list[0].SenderName);      // is_self → 我
        Assert.Equal("张晓明", list[1].SenderName);  // 对方
    }

    [Fact]
    public void Extract_RetrieveMemory_MapsRagChunk()
    {
        var json = """[{"chunk":{"session_id":"c1","session_name":"技术群","date":"2026-09-09","text":"[09:51] Anan: 聊到秋招","msg_count":3}}]""";

        var list = ToolEvidenceExtractor.Extract("retrieve_memory", "{}", json);

        var ev = Assert.Single(list);
        Assert.Equal("rag", ev.Source);
        Assert.Equal("技术群", ev.SessionDisplayName);
        Assert.Equal("2026-09-09", ev.Date);
    }

    [Theory]
    [InlineData("get_session_stats", """{"total_sessions":15}""")]   // 聚合结果不是原文
    [InlineData("list_sessions", """[{"username":"a"}]""")]
    [InlineData("search_messages", "不是 JSON（被截断的输出）")]
    [InlineData("search_messages", """{"error":"boom"}""")]          // 非数组
    public void Extract_NonCitableTool_ReturnsEmpty(string tool, string output)
        => Assert.Empty(ToolEvidenceExtractor.Extract(tool, "{}", output));

    [Fact]
    public void Extract_TruncatedJsonArray_SalvagesCompleteElements()
    {
        // 实测坑：read_messages/limit=100 的输出远超 MaxToolResultChars，会被截断并追加"...[截断]"，
        // JSON 不再合法 → 修复前直接丢掉全部证据（工具"查了但等于没查"）。
        var full = """[{"session_id":"s1","create_time":1,"sender":"a","display_name":"小明","is_self":false,"local_type":1,"content":"第一条"},{"session_id":"s1","create_time":2,"sender":"a","display_name":"小明","is_self":false,"local_type":1,"content":"第二条"}]""";
        var truncated = full[..(full.Length - 70)];   // 模拟在第二个元素中间被切断

        var list = ToolEvidenceExtractor.Extract("search_messages", "{}", truncated + "\n...[截断]");

        var ev = Assert.Single(list);                // 只抢救出完整的那一条
        Assert.Equal("第一条", ev.Content);
    }

    [Fact]
    public async Task AgentLoop_ToolOutput_BecomesEvidence()
    {
        var json = """[{"session_id":"s1","create_time":1788395549,"sender":"wxid_a","display_name":"紫薯","is_self":false,"local_type":1,"content":"秋招有消息了吗"}]""";
        var client = new ScriptedChatClient([
            new ChatResult
            {
                ToolCalls = [new ToolCallRequest { Id = "c1", Name = "search_messages", ArgumentsJson = """{"keyword":"秋招"}""" }],
                FinishReason = "tool_calls",
            },
            new ChatResult { Content = "聊过秋招这件事 [1]。", FinishReason = "stop" },
        ]);
        var loop = new AgentLoop(client, JsonTool("search_messages", json), Options());

        var result = await loop.RunAsync("sys", "查秋招");

        var ev = Assert.Single(result.Evidence);
        Assert.Equal("s1", ev.SessionId);
        Assert.Equal("秋招有消息了吗", ev.Content);
        Assert.Equal("聊过秋招这件事 [1]。", result.Answer);
    }

    [Fact]
    public async Task ToolCallingSkill_TellsModelWhoTheFollowUpIsAbout()
    {
        // 回归：追问"继续找他的最新记录"里没有名字。规划器会把用户的话重述成 goal（可能丢掉人名），
        // 会话上文必须一路带到执行模型面前，否则它只能瞎找（实测表现：跑去翻无关群聊）。
        var seen = new List<IReadOnlyList<ChatMessage>>();
        var client = new ScriptedChatClient([new ChatResult { Content = "好", FinishReason = "stop" }])
        {
            CallsHooks = m => seen.Add(m.ToList()),
        };
        var skill = new ToolCallingSkill(client, SearchTools(), Options());

        await skill.ExecuteAsync(new SkillRequest
        {
            Query = "找到该人的最新聊天记录并生成回复",
            OriginalQuery = "继续找那个人的最新记录并帮我回一句",
            LastPerson = "张晓明",
            Focus = "和张晓明的聊天",
            History = [new MemoryAssistant.Core.Agent.Answer.AnswerTurn("我和张晓明最近聊了什么", "你们聊了…")],
        }, CancellationToken.None);

        var task = seen.Single().First(m => m.Role == "user").Content!;
        Assert.Contains("继续找那个人的最新记录", task);        // 用户原话（规划器重述前的）
        Assert.Contains("本轮实际要完成", task);                 // 规划器的 goal
        Assert.Contains("张晓明", task);                        // 关键：对象还在
        Assert.Contains("会话上文", task);
    }

    [Fact]
    public async Task ToolCallingSkill_PassesToolEvidenceUp()
    {
        var json = """[{"session_id":"s1","create_time":100,"sender":"a","display_name":"小明","is_self":false,"local_type":1,"content":"内容"}]""";
        var client = new ScriptedChatClient([
            new ChatResult
            {
                ToolCalls = [new ToolCallRequest { Id = "c1", Name = "search_messages" }],
                FinishReason = "tool_calls",
            },
            new ChatResult { Content = "找到了 [1]。", FinishReason = "stop" },
        ]);
        var skill = new ToolCallingSkill(client, JsonTool("search_messages", json), Options());

        var r = await skill.ExecuteAsync(new SkillRequest { Query = "查" }, CancellationToken.None);

        Assert.True(r.Sufficient);
        Assert.Equal("s1", Assert.Single(r.Evidence).SessionId);
    }
}
