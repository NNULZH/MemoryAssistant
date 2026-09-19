using System.Net;
using System.Text;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Answer;
using MemoryAssistant.Core.Agent.Planner;
using MemoryAssistant.Core.Agent.Runtime;
using MemoryAssistant.Core.Agent.Skills;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Infrastructure.Agent;

namespace MemoryAssistant.Tests;

/// <summary>
/// 流式输出单测（零真实网络）：
///  - SSE 逐帧解析：正文/思考增量、tool_calls 分片拼装、usage、finish_reason；
///  - 进度事件透传：状态行 + 答案增量要能一路走到调用方（UI 才可能边跑边刷）。
/// </summary>
public sealed class StreamingTests
{
    /// <summary>用固定 SSE 报文冒充服务端（不联网）。</summary>
    private sealed class SseHandler(string body) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private static DeepSeekChatClient Client(HttpMessageHandler handler)
        => new(
            new LlmOptions { Model = "deepseek-flash", ApiKey = "test", BaseUrl = "https://example.invalid/v1" },
            logger: null,
            http: new HttpClient(handler));

    [Fact]
    public async Task ChatStreamAsync_AssemblesContentReasoningAndFragmentedToolCalls()
    {
        // 关键：arguments 是被切成几片的（真实 SSE 就是这么下发的），必须拼回完整 JSON
        const string sse = """
        data: {"choices":[{"delta":{"reasoning_content":"先搜关键词"}}]}

        data: {"choices":[{"delta":{"content":"我来查一下。"}}]}

        data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","type":"function","function":{"name":"search_messages","arguments":"{\"key"}}]}}]}

        data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"word\":\"秋招\"}"}}]}}]}

        data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":5}}

        data: [DONE]

        """;
        var handler = new SseHandler(sse);
        var deltas = new List<string>();
        var thinks = new List<string>();

        var r = await Client(handler).ChatStreamAsync(
            [new ChatMessage { Role = "user", Content = "查秋招" }],
            tools: null,
            onContentDelta: deltas.Add,
            onReasoningDelta: thinks.Add);

        Assert.Equal("我来查一下。", r.Content);
        Assert.Equal("先搜关键词", r.ReasoningContent);
        Assert.Equal("我来查一下。", Assert.Single(deltas));
        Assert.Equal("先搜关键词", Assert.Single(thinks));

        var call = Assert.Single(r.ToolCalls!);
        Assert.Equal("c1", call.Id);
        Assert.Equal("search_messages", call.Name);
        Assert.Equal("""{"keyword":"秋招"}""", call.ArgumentsJson);   // 分片拼装成功
        Assert.Equal("tool_calls", r.FinishReason);
        Assert.Equal(10, r.PromptTokens);
        Assert.Equal(5, r.CompletionTokens);

        // 流式请求必须真的带 stream:true，且思考过程不得回填进请求
        Assert.Contains("\"stream\":true", handler.LastRequestBody);
        Assert.DoesNotContain("reasoning_content", handler.LastRequestBody);
    }

    [Fact]
    public async Task ChatStreamAsync_ToleratesEmptyFramesAndUnknownFields()
    {
        const string sse = """
        data: 

        event: ping

        data: {"choices":[{"delta":{"content":"好"}}]}

        data: {"choices":[{"delta":{"content":"的"}}]}

        data: [DONE]
        """;

        var r = await Client(new SseHandler(sse)).ChatStreamAsync(
            [new ChatMessage { Role = "user", Content = "hi" }], null, null, null);

        Assert.Equal("好的", r.Content);
        Assert.Null(r.ToolCalls);
    }

    [Fact]
    public async Task ChatStreamAsync_NonSuccessStatus_Throws()
    {
        var handler = new StatusHandler(HttpStatusCode.BadRequest, """{"error":"bad request"}""");
        var client = Client(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.ChatStreamAsync([new ChatMessage { Role = "user", Content = "x" }], null, null, null));
    }

    private sealed class StatusHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    // ---- 进度事件透传 ----

    private sealed class FixedPlanner(AgentPlan plan) : IPlanner
    {
        public SkillCatalog Catalog { get; } = SkillCatalog.Default();

        public Task<AgentPlan> PlanAsync(
            string query, PlannerHint? hint = null, bool preferLlm = true, CancellationToken ct = default,
            Action<string>? onReasoningDelta = null)
            => Task.FromResult(plan);
    }

    /// <summary>产出证据为空、Draft 为空的能力：让回答走"作答器"这条路（这样才能观察增量）。</summary>
    private sealed class BareSkill : IAgentSkill
    {
        public string Name => "tools";
        public string Description => "单测用：无产出的能力";
        public Task<SkillResult> ExecuteAsync(SkillRequest request, CancellationToken ct)
            => Task.FromResult(new SkillResult { Skill = Name, Success = true, Sufficient = true, Summary = "查完了" });
    }

    private sealed class DeltaComposer : IAnswerComposer
    {
        public Task<string?> ComposeAsync(AnswerRequest request, CancellationToken ct, Action<string>? onDelta = null)
        {
            onDelta?.Invoke("你");
            onDelta?.Invoke("好");
            return Task.FromResult<string?>("你好");
        }
    }

    /// <summary>同时实现一次性与流式：流式时把结果里的 reasoning/content 当增量吐出来。</summary>
    private sealed class StreamingScript : IChatClient, IStreamingChatClient
    {
        private readonly Queue<ChatResult> _script;

        public StreamingScript(IEnumerable<ChatResult> script) => _script = new Queue<ChatResult>(script);

        public string ModelName => "mock-stream";
        public List<string> ThinkingDeltas { get; } = [];

        public Task<ChatResult> ChatAsync(
            IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolSchema>? tools = null, CancellationToken ct = default)
            => Task.FromResult(_script.Count > 0 ? _script.Dequeue() : new ChatResult { Content = "fallback" });

        public Task<ChatResult> ChatStreamAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolSchema>? tools,
            Action<string>? onContentDelta,
            Action<string>? onReasoningDelta,
            CancellationToken ct = default)
        {
            var r = _script.Count > 0 ? _script.Dequeue() : new ChatResult { Content = "fallback" };
            if (!string.IsNullOrEmpty(r.ReasoningContent))
            {
                ThinkingDeltas.Add(r.ReasoningContent);
                onReasoningDelta?.Invoke(r.ReasoningContent);
            }
            if (!string.IsNullOrEmpty(r.Content)) onContentDelta?.Invoke(r.Content);
            return Task.FromResult(r);
        }
    }

    private static ToolRegistry SingleTool(string name)
    {
        var registry = new ToolRegistry();
        registry.Register(new ToolDefinition
        {
            Name = name,
            Description = "单测用工具",
            ExecuteAsync = (_, _) => Task.FromResult(new ToolCallResult { Success = true, Output = "[]" }),
        });
        return registry;
    }

    [Fact]
    public async Task AgentLoop_StreamsReasoningToThinkingCallback()
    {
        // 时序要求：思考先推，答案后到——UI 才能"先看到模型在想什么，再看到回答"
        var client = new StreamingScript([
            new ChatResult
            {
                ReasoningContent = "先搜一下关键词",
                ToolCalls = [new ToolCallRequest { Id = "c1", Name = "search_messages" }],
                FinishReason = "tool_calls",
            },
            new ChatResult { ReasoningContent = "够了，可以答", Content = "答案是 X", FinishReason = "stop" },
        ]);
        var loop = new AgentLoop(client, SingleTool("search_messages"), new AgentOptions { MaxRounds = 5 });

        var thinking = new List<string>();
        var result = await loop.RunAsync("sys", "查", onThinking: thinking.Add);

        Assert.Equal(2, thinking.Count);
        Assert.Equal("先搜一下关键词", thinking[0]);
        Assert.Equal("够了，可以答", thinking[1]);
        Assert.Equal("答案是 X", result.Answer);
    }

    private sealed class ThinkingPlanner(AgentPlan plan) : IPlanner
    {
        public SkillCatalog Catalog { get; } = SkillCatalog.Default();

        public Task<AgentPlan> PlanAsync(
            string query, PlannerHint? hint = null, bool preferLlm = true, CancellationToken ct = default,
            Action<string>? onReasoningDelta = null)
        {
            onReasoningDelta?.Invoke("权衡该用哪个能力");
            return Task.FromResult(plan);
        }
    }

    [Fact]
    public async Task ConversationalAgent_PushesPlannerThinkingAsProgress()
    {
        // 规划思考要能在"正在理解你的问题…"之后、回答之前推给 UI
        var opts = new AgentOptions { EnableReplanning = false, MaxTaskSeconds = 0 };
        var planner = new ThinkingPlanner(new AgentPlan
        {
            Goal = "打个招呼",
            Steps = [new PlanStep { Id = "s1", Kind = PlanStepKind.Skill, Name = "tools" }],
        });
        var agent = new MemoryAssistant.Core.Agent.Conversation.ConversationalAgent(
            planner, new SkillRegistry([new BareSkill()]), opts,
            enableLlmPlanning: true, composer: new DeltaComposer());

        var seen = new List<AgentProgress>();
        await agent.RunAsync("你好", default, seen.Add);

        Assert.Contains(seen, p => p.Kind == AgentProgressKind.ThinkingDelta && p.Text.Contains("权衡"));
        // 思考必须出现在答案增量之前
        var firstThinking = seen.FindIndex(p => p.Kind == AgentProgressKind.ThinkingDelta);
        var firstAnswer = seen.FindIndex(p => p.Kind == AgentProgressKind.AnswerDelta);
        Assert.True(firstThinking >= 0 && firstAnswer > firstThinking);
    }

    [Fact]
    public async Task ConversationalAgent_PushesStatusAndAnswerDeltas()
    {
        var opts = new AgentOptions { EnableReplanning = false, MaxTaskSeconds = 0 };
        var planner = new FixedPlanner(new AgentPlan
        {
            Goal = "打个招呼",
            Steps = [new PlanStep { Id = "s1", Kind = PlanStepKind.Skill, Name = "tools" }],
        });
        var agent = new MemoryAssistant.Core.Agent.Conversation.ConversationalAgent(
            planner, new SkillRegistry([new BareSkill()]), opts,
            enableLlmPlanning: false, composer: new DeltaComposer());

        var seen = new List<AgentProgress>();
        var result = await agent.RunAsync("你好", default, seen.Add);

        Assert.Equal("你好", result.Answer);
        Assert.Contains(seen, p => p.Kind == AgentProgressKind.Status && p.Text.Contains("tools"));
        Assert.Equal("你好", string.Concat(
            seen.Where(p => p.Kind == AgentProgressKind.AnswerDelta).Select(p => p.Text)));
    }
}
