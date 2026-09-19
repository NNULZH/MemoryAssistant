using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.PythonBridge;
using MemoryAssistant.Infrastructure.Agent;

namespace MemoryAssistant.Tests;

/// <summary>可控的 IPythonBridge 桩。</summary>
public sealed class StubBridge : IPythonBridge
{
    public bool IsRunning => true;
    public List<(string Method, IReadOnlyDictionary<string, object?> Args)> Calls { get; } = [];
    public Func<string, BridgeResponse>? Responder { get; set; }

    public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task StopAsync() => Task.CompletedTask;

    public Task<BridgeResponse> RequestAsync(
        string method,
        IReadOnlyDictionary<string, object?>? args = null,
        int? timeoutSeconds = null,
        CancellationToken ct = default)
    {
        Calls.Add((method, args ?? new Dictionary<string, object?>()));
        var resp = Responder?.Invoke(method)
            ?? new BridgeResponse("x", true, new Dictionary<string, object?>(), null);
        return Task.FromResult(resp);
    }

    public void Dispose() { }
}

public class BridgeToolProviderTests
{
    private static (ToolRegistry Registry, StubBridge Bridge) Setup()
    {
        var bridge = new StubBridge();
        var registry = new ToolRegistry();
        new BridgeToolProvider(bridge, new MemoryAssistant.Core.Configuration.AgentOptions { MaxToolResultChars = 100 })
            .RegisterAll(registry);
        return (registry, bridge);
    }

    [Fact]
    public void RegisterAll_RegistersSevenTools()
    {
        var (registry, _) = Setup();
        Assert.Equal(7, registry.All.Count);
        Assert.Equal(7, registry.BuildSchemas().Count);
        // find_sessions 是"按人名找会话"的唯一正确入口（list_sessions 的关键词会误命中群聊），
        // 必须一直挂在注册表里，否则模型只能靠猜会话 id。
        Assert.Contains(registry.All, t => t.Name == "find_sessions");
    }

    [Fact]
    public void RegisterAll_SchemasContainNameAndDescription()
    {
        var (registry, _) = Setup();
        var schemas = registry.BuildSchemas();
        Assert.All(schemas, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Name));
            Assert.False(string.IsNullOrWhiteSpace(s.Description));
        });
    }

    [Fact]
    public async Task ExecuteAsync_PassesDefaultsAndMarshalsOutput()
    {
        var (registry, bridge) = Setup();
        bridge.Responder = m => new BridgeResponse("x", true,
            new Dictionary<string, object?> { ["total"] = 42 }, null);

        var tool = registry.All.Single(t => t.Name == "list_sessions");
        var result = await tool.ExecuteAsync("{}", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("42", result.Output);
        var (method, args) = bridge.Calls.Single();
        Assert.Equal("list_sessions", method);
        Assert.Equal(20, args["limit"]); // 默认值填充（int）
        Assert.Equal("", args["keyword"]);
    }

    [Fact]
    public async Task ExecuteAsync_OverridesDefaultsFromModel()
    {
        var (registry, bridge) = Setup();
        var tool = registry.All.Single(t => t.Name == "list_sessions");
        await tool.ExecuteAsync("{\"limit\": 3}", CancellationToken.None);

        var (_, args) = bridge.Calls.Single();
        Assert.Equal(3L, args["limit"]);
    }

    [Fact]
    public async Task ExecuteAsync_BridgeFailure_ReturnsError()
    {
        var (registry, bridge) = Setup();
        bridge.Responder = m => new BridgeResponse("x", false, null, "boom");
        var tool = registry.All.Single(t => t.Name == "search_messages");
        var result = await tool.ExecuteAsync("{\"keyword\":\"工资\"}", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("boom", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_LargeOutput_IsTruncated()
    {
        var (registry, bridge) = Setup();
        var big = new string('x', 500);
        bridge.Responder = m => new BridgeResponse("x", true, new { content = big }, null);
        var tool = registry.All.Single(t => t.Name == "read_messages");
        var result = await tool.ExecuteAsync("{\"session_id\":\"s1\"}", CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.IsOmitted);
        Assert.True(result.Output.Length < 200);
        Assert.Contains("截断", result.Output);
    }
}
