using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Configuration;

namespace MemoryAssistant.Tests;

public class AgentLoopTests
{
    private static AgentOptions Options() => new() { MaxRounds = 5, MaxToolResultChars = 4000 };

    private static ToolRegistry TwoTools()
    {
        var registry = new ToolRegistry();
        registry.Register(new ToolDefinition
        {
            Name = "echo",
            Description = "回显输入",
            ExecuteAsync = (args, _) => Task.FromResult(new ToolCallResult { Success = true, Output = $"echo:{args}" }),
        });
        registry.Register(new ToolDefinition
        {
            Name = "fail",
            Description = "总是失败",
            ExecuteAsync = (_, _) => Task.FromResult(new ToolCallResult { Success = false, Error = "boom" }),
        });
        return registry;
    }

    [Fact]
    public async Task RunAsync_NoToolCalls_ReturnsAnswerImmediately()
    {
        var client = new ScriptedChatClient([
            new ChatResult { Content = "直接回答", FinishReason = "stop" },
        ]);
        var loop = new AgentLoop(client, TwoTools(), Options());

        var result = await loop.RunAsync("sys", "你好");

        Assert.True(result.CompletedNormally);
        Assert.Equal("直接回答", result.Answer);
        Assert.Empty(result.AllToolCalls);
        Assert.Equal(1, result.RoundCount);
        Assert.Single(client.Calls);
        // 工具 schema 始终传给 LLM（注册了 2 个），但本轮无工具调用。
        Assert.Equal(2, client.Calls[0].ToolCount);
    }

    [Fact]
    public async Task RunAsync_ToolCall_ThenAnswer_TracesBoth()
    {
        var client = new ScriptedChatClient([
            new ChatResult
            {
                ToolCalls = [new ToolCallRequest { Id = "c1", Name = "echo", ArgumentsJson = "{\"x\":1}" }],
            },
            new ChatResult { Content = "这是基于工具的答案", FinishReason = "stop" },
        ]);
        var loop = new AgentLoop(client, TwoTools(), Options());

        var result = await loop.RunAsync("sys", "帮我查");

        Assert.True(result.CompletedNormally);
        Assert.Equal("这是基于工具的答案", result.Answer);
        Assert.Single(result.AllToolCalls);
        Assert.Equal(2, result.RoundCount);
        // 第二轮调用时工具 schema 仍为 2 个（tools 参数每轮都传）。
        Assert.Equal(2, client.Calls[1].ToolCount);
    }

    // 工具调用必须单独推给人看：只有"思考"的话，模型说"我去查一下"和它真的查了长得一模一样。
    [Fact]
    public async Task RunAsync_ToolCall_EmitsStartAndFinishProgress()
    {
        var client = new ScriptedChatClient([
            new ChatResult
            {
                ToolCalls = [new ToolCallRequest { Id = "c1", Name = "echo", ArgumentsJson = "{\"x\":1,\"kw\":\"秋招\"}" }],
            },
            new ChatResult { Content = "答完了", FinishReason = "stop" },
        ]);
        var loop = new AgentLoop(client, TwoTools(), Options());
        var events = new List<AgentProgress>();

        await loop.RunAsync("sys", "查", onTool: events.Add);

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(AgentProgressKind.ToolCall, e.Kind));

        Assert.Equal("start", events[0].Phase);
        Assert.Equal("echo", events[0].ToolName);
        Assert.Equal("x=1, kw=秋招", events[0].ToolArgs);   // JSON 压成一行 k=v，别把原始 JSON 糊到脸上

        Assert.Equal("finish", events[1].Phase);
        Assert.Equal("echo", events[1].ToolName);
        Assert.True(events[1].ToolSuccess);
        Assert.Contains("echo:", events[1].ToolSummary);
    }

    [Fact]
    public async Task RunAsync_FailedTool_EmitsFailureWithReason()
    {
        var client = new ScriptedChatClient([
            new ChatResult { ToolCalls = [new ToolCallRequest { Id = "c1", Name = "fail" }] },
            new ChatResult { Content = "工具挂了", FinishReason = "stop" },
        ]);
        var loop = new AgentLoop(client, TwoTools(), Options());
        var events = new List<AgentProgress>();

        await loop.RunAsync("sys", "查", onTool: events.Add);

        var finish = Assert.Single(events, e => e.Phase == "finish");
        Assert.False(finish.ToolSuccess);
        Assert.Contains("boom", finish.ToolSummary);       // 失败也要如实显示原因，不能只显示"完成"
    }

    [Fact]
    public void CompactArguments_HandlesBadJsonWithoutLosingIt()
    {
        Assert.Equal("", AgentProgress.CompactArguments(null));
        Assert.Equal("随便一句话", AgentProgress.CompactArguments("随便一句话"));   // 不是 JSON → 原样带出来
        Assert.Equal("a=1, b=true, c=", AgentProgress.CompactArguments("""{"a":1,"b":true,"c":null}"""));
    }

    [Fact]
    public async Task RunAsync_ToolCall_ResultIsFedBack()
    {
        string? fedBack = null;
        var client = new ScriptedChatClient([
            new ChatResult
            {
                ToolCalls = [new ToolCallRequest { Id = "c1", Name = "echo", ArgumentsJson = "{\"x\":42}" }],
            },
            new ChatResult { Content = "done", FinishReason = "stop" },
        ]);
        client.CallsHooks = msgs =>
        {
            fedBack = msgs.LastOrDefault(m => m.Role == "tool")?.Content;
        };

        var loop = new AgentLoop(client, TwoTools(), Options());
        await loop.RunAsync("sys", "查");
        Assert.Equal("echo:{\"x\":42}", fedBack);
    }

    [Fact]
    public async Task RunAsync_ToolCallWithoutContent_KeepsAssistantToolCallsInHistory()
    {
        // 回归：模型发起工具调用时 content 通常为空。若此时不把 assistant(tool_calls) 写入历史，
        // 回填的 tool 消息就会缺少前驱，OpenAI/DeepSeek 会直接 400（实战踩到过）。
        IReadOnlyList<ChatMessage>? secondCallMessages = null;
        int call = 0;
        var client = new ScriptedChatClient([
            new ChatResult
            {
                Content = null,   // 关键：无正文，只有工具调用
                ToolCalls = [new ToolCallRequest { Id = "c1", Name = "echo", ArgumentsJson = "{}" }],
            },
            new ChatResult { Content = "done", FinishReason = "stop" },
        ]);
        client.CallsHooks = msgs =>
        {
            if (++call == 2) secondCallMessages = msgs.ToList();
        };

        var loop = new AgentLoop(client, TwoTools(), Options());
        await loop.RunAsync("sys", "查");

        Assert.NotNull(secondCallMessages);
        var assistantIndex = secondCallMessages!.ToList().FindIndex(m => m.Role == "assistant" && m.ToolCalls is { Count: > 0 });
        var toolIndex = secondCallMessages.ToList().FindIndex(m => m.Role == "tool" && m.ToolCallId == "c1");
        Assert.True(assistantIndex >= 0, "第二轮历史里应有带 tool_calls 的 assistant 消息");
        Assert.True(toolIndex > assistantIndex, "tool 结果必须排在其前驱 assistant(tool_calls) 之后");
    }

    [Fact]
    public async Task RunAsync_FailedTool_FailureIsFedBackToModel()
    {
        string? fedBack = null;
        var client = new ScriptedChatClient([
            new ChatResult { ToolCalls = [new ToolCallRequest { Id = "c1", Name = "fail" }] },
            new ChatResult { Content = "done", FinishReason = "stop" },
        ]);
        client.CallsHooks = msgs => fedBack = msgs.LastOrDefault(m => m.Role == "tool")?.Content;

        var loop = new AgentLoop(client, TwoTools(), Options());
        await loop.RunAsync("sys", "查");

        Assert.NotNull(fedBack);
        Assert.Contains("ERROR", fedBack!);
        Assert.Contains("boom", fedBack!);
    }

    [Fact]
    public async Task RunAsync_UnknownTool_ReturnsErrorAndContinues()
    {
        var client = new ScriptedChatClient([
            new ChatResult
            {
                ToolCalls = [new ToolCallRequest { Id = "c1", Name = "nope" }],
            },
            new ChatResult { Content = "已处理", FinishReason = "stop" },
        ]);
        var loop = new AgentLoop(client, TwoTools(), Options());

        var result = await loop.RunAsync("sys", "查");

        Assert.True(result.CompletedNormally);
        Assert.Single(result.AllToolCalls);
    }

    [Fact]
    public async Task RunAsync_AlwaysToolCalls_StopsAtMaxRounds()
    {
        var client = new ScriptedChatClient([
            new ChatResult { ToolCalls = [new ToolCallRequest { Id = "c1", Name = "echo" }] },
            new ChatResult { ToolCalls = [new ToolCallRequest { Id = "c2", Name = "echo" }] },
            new ChatResult { ToolCalls = [new ToolCallRequest { Id = "c3", Name = "echo" }] },
            new ChatResult { ToolCalls = [new ToolCallRequest { Id = "c4", Name = "echo" }] },
            new ChatResult { ToolCalls = [new ToolCallRequest { Id = "c5", Name = "echo" }] },
            new ChatResult { Content = "被迫收尾", FinishReason = "stop" },
        ]);
        var loop = new AgentLoop(client, TwoTools(), Options());

        var result = await loop.RunAsync("sys", "查");

        Assert.False(result.CompletedNormally);
        Assert.Equal("max_rounds(5)", result.EarlyStopReason);
        Assert.Equal(5, result.RoundCount);
        Assert.Equal(6, client.Calls.Count); // 5 轮带工具 + 1 次强制收尾
        Assert.Contains("被迫收尾", result.Answer);
    }

    [Fact]
    public async Task RunAsync_Cancellation_Propagates()
    {
        var client = new ScriptedChatClient([
            new ChatResult { ToolCalls = [new ToolCallRequest { Id = "c1", Name = "echo" }] },
        ]);
        var loop = new AgentLoop(client, TwoTools(), Options());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.RunAsync("sys", "查", ct: cts.Token));
    }

    [Fact]
    public async Task RunAsync_FailingTool_ResultCarriesError()
    {
        ToolCallResult? seen = null;
        var client = new ScriptedChatClient([
            new ChatResult
            {
                ToolCalls = [new ToolCallRequest { Id = "c1", Name = "fail" }],
            },
            new ChatResult { Content = "ok", FinishReason = "stop" },
        ]);
        client.CallsHooks = msgs =>
        {
            var t = msgs.LastOrDefault(m => m.Role == "tool");
            if (t is not null) seen = new ToolCallResult { Success = false, Error = t.Content };
        };

        var loop = new AgentLoop(client, TwoTools(), Options());
        var result = await loop.RunAsync("sys", "查");

        Assert.True(result.CompletedNormally);
        Assert.NotNull(seen);
        Assert.False(seen!.Success);
    }
}
