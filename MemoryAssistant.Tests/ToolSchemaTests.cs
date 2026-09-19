using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Infrastructure.Agent;

namespace MemoryAssistant.Tests;

/// <summary>
/// P14 Tool Schema 2.0 单测：验证 BuildSchemas() 产出真实 JSON Schema
/// （properties 非空 / required / type / min-max / 分类），以及 spec 驱动的默认值填充与 Eco 钳制。
/// </summary>
public sealed class ToolSchemaTests
{
    private static (ToolRegistry Registry, StubBridge Bridge) Setup(AgentOptions? options = null)
    {
        var bridge = new StubBridge();
        var registry = new ToolRegistry();
        new BridgeToolProvider(bridge, options ?? new AgentOptions()).RegisterAll(registry);
        return (registry, bridge);
    }

    private static JsonObject Parameters(ToolRegistry registry, string name)
    {
        var schema = registry.BuildSchemas().Single(s => s.Name == name);
        Assert.NotNull(schema.Parameters);
        return schema.Parameters!;
    }

    [Fact]
    public void AllBridgeTools_PropertiesAreNotEmpty()
    {
        // 旧实现的技术债：properties 统一为空 → 模型看不到参数。修复后每个工具都应有真实参数。
        var (registry, _) = Setup();
        var schemas = registry.BuildSchemas();
        Assert.Equal(7, schemas.Count);
        foreach (var s in schemas)
        {
            var props = s.Parameters!["properties"]!.AsObject();
            Assert.NotEmpty(props);
            Assert.Equal("object", s.Parameters!["type"]!.GetValue<string>());
        }
    }

    [Fact]
    public void ReadMessages_RequiredAndRangesAreExposed()
    {
        var (registry, _) = Setup();
        var p = Parameters(registry, "read_messages");

        var required = p["required"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains("session_id", required);

        var sessionId = p["properties"]!["session_id"]!.AsObject();
        Assert.Equal("string", sessionId["type"]!.GetValue<string>());

        var limit = p["properties"]!["limit"]!.AsObject();
        Assert.Equal("integer", limit["type"]!.GetValue<string>());
        Assert.Equal(1L, limit["minimum"]!.GetValue<long>());
        Assert.Equal(200L, limit["maximum"]!.GetValue<long>());
    }

    [Fact]
    public void SearchMessages_KeywordRequired()
    {
        var (registry, _) = Setup();
        var p = Parameters(registry, "search_messages");
        var required = p["required"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains("keyword", required);
        Assert.Equal("integer", p["properties"]!["limit"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Description_IsPrefixedWithCategory()
    {
        var (registry, _) = Setup();
        var readSchema = registry.BuildSchemas().Single(s => s.Name == "read_messages");
        Assert.StartsWith("[Memory]", readSchema.Description);

        var statsSchema = registry.BuildSchemas().Single(s => s.Name == "get_session_stats");
        Assert.StartsWith("[Analysis]", statsSchema.Description);
    }

    [Fact]
    public void ToolDefinition_SchemaJson_IsDeepSeekCompatible()
    {
        // DeepSeek 约束：parameters 必须 {type:object, properties:{...}}（空对象会被拒）。
        var (registry, _) = Setup();
        foreach (var s in registry.BuildSchemas())
        {
            var json = s.Parameters!.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
            Assert.True(doc.RootElement.TryGetProperty("properties", out var props));
            Assert.True(props.EnumerateObject().Any());
        }
    }

    [Fact]
    public async Task DefaultFill_AndEcoClamp_WorkTogether()
    {
        var opts = new AgentOptions { EcoMode = true, EcoMaxReadLimit = 15 };
        var (registry, bridge) = Setup(opts);
        var tool = registry.All.Single(t => t.Name == "read_messages");
        var result = await tool.ExecuteAsync("{\"session_id\":\"s1\"}", CancellationToken.None);

        Assert.True(result.Success);
        var (method, args) = bridge.Calls.Single();
        Assert.Equal("read_messages", method);
        Assert.Equal("s1", args["session_id"]);
        Assert.Equal(15, args["limit"]); // 默认 30 → Eco 钳到 15
    }

    [Fact]
    public async Task Defaults_AreFilled_WhenModelOmits()
    {
        var (registry, bridge) = Setup();
        var tool = registry.All.Single(t => t.Name == "list_sessions");
        await tool.ExecuteAsync("{}", CancellationToken.None);

        var (_, args) = bridge.Calls.Single();
        Assert.Equal(20, args["limit"]);
        Assert.Equal("", args["keyword"]);
    }

    // ---------- 注册期校验 ----------

    [Fact]
    public void Register_Rejects_IllegalCategory()
    {
        var registry = new ToolRegistry();
        Assert.Throws<ArgumentException>(() => registry.Register(new ToolDefinition
        {
            Name = "evil",
            Description = "x",
            Category = "Hack",
            ExecuteAsync = (_, _) => Task.FromResult(new ToolCallResult { Success = true }),
        }));
    }

    [Fact]
    public void Register_Rejects_IllegalParamType()
    {
        var registry = new ToolRegistry();
        Assert.Throws<ArgumentException>(() => registry.Register(new ToolDefinition
        {
            Name = "bad",
            Description = "x",
            Parameters = [new ToolParameterSpec { Name = "p", Type = "float" }],
            ExecuteAsync = (_, _) => Task.FromResult(new ToolCallResult { Success = true }),
        }));
    }

    [Fact]
    public void Register_ActionTool_MustBeExplicitlyNonReadOnly()
    {
        var registry = new ToolRegistry();
        Assert.Throws<ArgumentException>(() => registry.Register(new ToolDefinition
        {
            Name = "write",
            Description = "x",
            Category = ToolCategory.Action,
            ExecuteAsync = (_, _) => Task.FromResult(new ToolCallResult { Success = true }),
        }));
        // ReadOnly=false 显式声明后允许注册（2.0 预留 Action，不开放给 Agent）
        var def = new ToolDefinition
        {
            Name = "write",
            Description = "x",
            Category = ToolCategory.Action,
            ReadOnly = false,
            ExecuteAsync = (_, _) => Task.FromResult(new ToolCallResult { Success = true }),
        };
        registry.Register(def);
        Assert.Single(registry.All);
    }
}
