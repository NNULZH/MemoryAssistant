using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Configuration;

namespace MemoryAssistant.Tests;

public class AgentTraceTests
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
    public async Task RunAsync_MultiRound_ProducesRoundTraces()
    {
        var client = new ScriptedChatClient([
            new ChatResult
            {
                ToolCalls = [new ToolCallRequest { Id = "c1", Name = "echo", ArgumentsJson = "{\"x\":1}" }],
                PromptTokens = 100, CompletionTokens = 20, FinishReason = "tool_calls",
            },
            new ChatResult { Content = "答案", PromptTokens = 50, CompletionTokens = 30, FinishReason = "stop" },
        ]);
        var loop = new AgentLoop(client, TwoTools(), Options());

        var result = await loop.RunAsync("sys", "查");

        Assert.Equal(2, result.RoundCount);
        Assert.Equal(2, result.Rounds.Count);
        Assert.Equal(150, result.TotalPromptTokens);
        Assert.Equal(50, result.TotalCompletionTokens);
        Assert.Single(result.AllToolCalls); // 兼容旧字段
        var round1 = result.Rounds[0];
        Assert.Equal(1, round1.Round);
        Assert.True(round1.LatencyMs >= 0);
        Assert.Equal("tool_calls", round1.FinishReason);
        Assert.Single(round1.ToolCalls);
        Assert.True(round1.ToolCalls[0].Success);
        Assert.Equal("echo", round1.ToolCalls[0].Name);
        Assert.Equal(1, round1.ToolCalls[0].Round);
        Assert.Equal(2, result.Rounds[1].Round);
        Assert.Empty(result.Rounds[1].ToolCalls);
    }

    [Fact]
    public async Task RunAsync_FailingTool_TraceCarriesError()
    {
        var client = new ScriptedChatClient([
            new ChatResult { ToolCalls = [new ToolCallRequest { Id = "c1", Name = "fail" }] },
            new ChatResult { Content = "ok", FinishReason = "stop" },
        ]);
        var loop = new AgentLoop(client, TwoTools(), Options());

        var result = await loop.RunAsync("sys", "查");

        var trace = result.Rounds[0].ToolCalls[0];
        Assert.False(trace.Success);
        Assert.Equal("boom", trace.Error);
    }

    [Fact]
    public async Task RunAsync_UnknownTool_TraceError()
    {
        var client = new ScriptedChatClient([
            new ChatResult { ToolCalls = [new ToolCallRequest { Id = "c1", Name = "nope" }] },
            new ChatResult { Content = "已处理", FinishReason = "stop" },
        ]);
        var loop = new AgentLoop(client, TwoTools(), Options());

        var result = await loop.RunAsync("sys", "查");

        var trace = result.Rounds[0].ToolCalls[0];
        Assert.False(trace.Success);
        Assert.Contains("未知工具", trace.Error);
    }

    [Fact]
    public async Task RunAsync_MaxRounds_RoundsIncludeForcedFinal()
    {
        var client = new ScriptedChatClient([
            new ChatResult { ToolCalls = [new ToolCallRequest { Id = "c1", Name = "echo" }] },
            new ChatResult { ToolCalls = [new ToolCallRequest { Id = "c2", Name = "echo" }] },
            new ChatResult { ToolCalls = [new ToolCallRequest { Id = "c3", Name = "echo" }] },
            new ChatResult { ToolCalls = [new ToolCallRequest { Id = "c4", Name = "echo" }] },
            new ChatResult { ToolCalls = [new ToolCallRequest { Id = "c5", Name = "echo" }] },
            new ChatResult { Content = "被迫收尾", FinishReason = "stop", PromptTokens = 10, CompletionTokens = 5 },
        ]);
        var loop = new AgentLoop(client, TwoTools(), Options());

        var result = await loop.RunAsync("sys", "查");

        Assert.Equal(5, result.RoundCount);               // 旧语义不变
        Assert.Equal(6, result.Rounds.Count);             // 含强制收尾轮
        Assert.Equal(6, result.Rounds[5].Round);
        Assert.Equal(10, result.TotalPromptTokens);
        Assert.Equal(5, result.TotalCompletionTokens);
    }

    [Fact]
    public async Task RunAsync_OmitsUsage_TokensZero()
    {
        var client = new ScriptedChatClient([
            new ChatResult { Content = "直接回答" },
        ]);
        var loop = new AgentLoop(client, TwoTools(), Options());

        var result = await loop.RunAsync("sys", "你好");

        Assert.Single(result.Rounds);
        Assert.Equal(0, result.Rounds[0].PromptTokens);
        Assert.Equal(0, result.TotalPromptTokens);
    }

    [Fact]
    public async Task RunAsync_TruncatesLongOutput()
    {
        var longArgs = new string('a', 500);
        var client = new ScriptedChatClient([
            new ChatResult { ToolCalls = [new ToolCallRequest { Id = "c1", Name = "echo", ArgumentsJson = longArgs }] },
            new ChatResult { Content = "ok", FinishReason = "stop" },
        ]);
        var loop = new AgentLoop(client, TwoTools(), Options());

        var result = await loop.RunAsync("sys", "查");

        var trace = result.Rounds[0].ToolCalls[0];
        Assert.True(trace.ArgumentsJson.Length <= 303);
    }
}
