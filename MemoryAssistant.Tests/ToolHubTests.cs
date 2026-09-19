using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Tools;
using MemoryAssistant.Core.Configuration;

namespace MemoryAssistant.Tests;

/// <summary>V3.5 单测：工具清单解析 / 外部工具执行 / 热注册上下架（零网络、零 LLM）。</summary>
public sealed class ToolHubTests
{
    // ---------- 清单解析 ----------

    private const string ValidManifest = """
    {
      "tools": [
        {
          "name": "weather_now",
          "description": "查天气",
          "category": "Context",
          "readOnly": true,
          "method": "GET",
          "url": "http://127.0.0.1:9/w?city={city}&days={days}",
          "parameters": [
            { "name": "city", "type": "string", "description": "城市", "required": true },
            { "name": "days", "type": "integer", "default": 1 }
          ]
        }
      ]
    }
    """;

    [Fact]
    public void Parse_ReadsToolWithRealParameters()
    {
        var (tools, rejected) = ToolManifest.Parse(ValidManifest);

        Assert.Empty(rejected);
        var tool = Assert.Single(tools);
        Assert.Equal("weather_now", tool.Name);
        Assert.Equal("GET", tool.Endpoint.Method);
        Assert.Equal(ToolCategory.Context, tool.Category);
        Assert.True(tool.ReadOnly);
        Assert.Equal(2, tool.Parameters.Count);
        Assert.True(tool.Parameters[0].Required);
        Assert.Equal(ToolParamType.Integer, tool.Parameters[1].Type);
        Assert.Equal(1L, tool.Parameters[1].Default);   // 整数默认值归一为 long，避免出现 1.0 形式
    }

    [Theory]
    [InlineData("""{"tools":[{"name":"x","category":"Nope","url":"http://a"}]}""", "非法分类")]
    [InlineData("""{"tools":[{"name":"x","category":"Context"}]}""", "缺少 url")]
    [InlineData("""{"tools":[{"name":"x","category":"Context","url":"u","method":"PUT"}]}""", "暂不支持的方法")]
    [InlineData("""{"tools":[{"name":"x","category":"Action","url":"u","readOnly":true}]}""", "Action 类工具必须显式")]
    [InlineData("""{"tools":[{"name":"x","category":"Context","url":"u","parameters":[{"name":"p","type":"money"}]}]}""", "类型非法")]
    public void Parse_RejectsBadEntriesWithReason(string json, string expectedReason)
    {
        var (tools, rejected) = ToolManifest.Parse(json);
        Assert.Empty(tools);
        Assert.Contains(rejected, r => r.Contains(expectedReason));
    }

    [Fact]
    public void Parse_BrokenJson_IsReportedNotThrown()
    {
        var (tools, rejected) = ToolManifest.Parse("{ not json");
        Assert.Empty(tools);
        Assert.Contains(rejected, r => r.Contains("不是合法 JSON"));
    }

    // ---------- 外部工具执行（用假 invoker，不打网络）----------

    private sealed class FakeInvoker : IToolInvoker
    {
        public RemoteToolEndpoint? LastEndpoint { get; private set; }
        public IReadOnlyDictionary<string, object?>? LastArgs { get; private set; }

        public Task<ToolCallResult> InvokeAsync(
            RemoteToolEndpoint endpoint, IReadOnlyDictionary<string, object?> args, CancellationToken ct)
        {
            LastEndpoint = endpoint;
            LastArgs = args;
            return Task.FromResult(new ToolCallResult { Success = true, Output = $"called:{endpoint.Url}" });
        }
    }

    [Fact]
    public async Task Execute_PassesArgsAndFillsDefaults()
    {
        var (tools, _) = ToolManifest.Parse(ValidManifest);
        var invoker = new FakeInvoker();
        var def = ToolManifest.ToToolDefinitions(tools, invoker).Single();

        var result = await def.ExecuteAsync("""{"city":"上海"}""", CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(invoker.LastArgs);
        Assert.Equal("上海", invoker.LastArgs!["city"]);
        Assert.Equal(1L, invoker.LastArgs["days"]);          // 缺省值按声明补上
        Assert.Equal(ToolCategory.Context, def.Category);
    }

    // ---------- 热注册 ----------

    private static ToolDefinition Tool(string name, string category = ToolCategory.Context)
        => new()
        {
            Name = name,
            Description = name,
            Category = category,
            ExecuteAsync = (_, _) => Task.FromResult(new ToolCallResult { Success = true }),
        };

    [Fact]
    public void Hub_ReplacesScope_AndRaisesChange()
    {
        var registry = new ToolRegistry();
        var hub = new ToolHub(registry);
        var changes = new List<ToolHubChange>();
        hub.Changed += changes.Add;

        hub.ReplaceScope("a.tool.json", [Tool("t1"), Tool("t2")]);
        Assert.True(registry.TryGet("t1", out _));
        Assert.Equal(2, registry.All.Count);
        Assert.Equal(["t1", "t2"], hub.ToolsOf("a.tool.json").Order());

        // 同一来源重载：旧工具必须撤下，不能残留
        hub.ReplaceScope("a.tool.json", [Tool("t3")]);
        Assert.False(registry.TryGet("t1", out _));
        Assert.False(registry.TryGet("t2", out _));
        Assert.True(registry.TryGet("t3", out _));
        Assert.Single(registry.All);

        Assert.Equal(2, changes.Count);
        Assert.Contains("t3", changes[^1].Added);
        Assert.Contains("t1", changes[^1].Removed);
    }

    [Fact]
    public void Hub_RemoveScope_TakesToolsDown()
    {
        var registry = new ToolRegistry();
        var hub = new ToolHub(registry);
        hub.ReplaceScope("b.tool.json", [Tool("x")]);

        var change = hub.RemoveScope("b.tool.json");

        Assert.False(registry.TryGet("x", out _));
        Assert.Empty(registry.All);
        Assert.Contains("x", change.Removed);
        Assert.Empty(hub.ToolsOf("b.tool.json"));
    }

    [Fact]
    public void Hub_KeepsOtherScopesIntact()
    {
        var registry = new ToolRegistry();
        var hub = new ToolHub(registry);
        hub.ReplaceScope("a.tool.json", [Tool("a1")]);
        hub.ReplaceScope("b.tool.json", [Tool("b1")]);

        hub.RemoveScope("a.tool.json");

        Assert.False(registry.TryGet("a1", out _));
        Assert.True(registry.TryGet("b1", out _));
        Assert.Single(registry.All);
    }

    [Fact]
    public void Registry_Unregister_RemovesTool()
    {
        var registry = new ToolRegistry();
        registry.Register(Tool("solo"));
        Assert.True(registry.Unregister("solo"));
        Assert.False(registry.TryGet("solo", out _));
        Assert.False(registry.Unregister("solo"));
    }

    // ---------- 通用工具 Skill（V3.5b）：让主链路"用"上工具 ----------

    private static Task<MemoryAssistant.Core.Agent.Skills.SkillResult> RunToolSkill(
        MemoryAssistant.Core.Agent.Skills.ToolCallingSkill skill, string query)
        => skill.ExecuteAsync(
            new MemoryAssistant.Core.Agent.Skills.SkillRequest { Query = query }, CancellationToken.None);

    [Fact]
    public async Task ToolSkill_WithoutLlm_DegradesHonestly()
    {
        var skill = new MemoryAssistant.Core.Agent.Skills.ToolCallingSkill(
            chat: null, new ToolRegistry(), new AgentOptions());

        var r = await RunToolSkill(skill, "用工具查天气");

        Assert.True(r.Success);
        Assert.False(r.Sufficient);
        Assert.Contains("未配置 LLM", r.Summary);
    }

    [Fact]
    public async Task ToolSkill_InEcoMode_DoesNotSpendTokens()
    {
        var chat = new ScriptedChatClient([]);
        var skill = new MemoryAssistant.Core.Agent.Skills.ToolCallingSkill(
            chat, new ToolRegistry(), new AgentOptions { EcoMode = true });

        var r = await RunToolSkill(skill, "用工具查天气");

        Assert.False(r.Sufficient);
        Assert.Contains("Eco", r.Summary);
        Assert.Empty(chat.Calls);   // 一次 LLM 调用都没发
    }

    [Fact]
    public async Task ToolSkill_UsesToolRegisteredAfterSkillConstructed()
    {
        var registry = new ToolRegistry();
        var chat = new ScriptedChatClient([
            new ChatResult
            {
                ToolCalls = [new ToolCallRequest { Name = "weather_now", ArgumentsJson = """{"city":"上海"}""" }],
            },
            new ChatResult { Content = "上海今天晴，25℃" },
        ]);

        // 先建 Skill，再热注册工具 —— 模拟"运行中丢进来一个新工具清单"
        var skill = new MemoryAssistant.Core.Agent.Skills.ToolCallingSkill(chat, registry, new AgentOptions());
        registry.Register(new ToolDefinition
        {
            Name = "weather_now",
            Description = "查天气",
            Category = ToolCategory.Context,
            ReadOnly = true,
            Parameters = [new ToolParameterSpec { Name = "city", Type = ToolParamType.String, Required = true }],
            ExecuteAsync = (_, _) => Task.FromResult(new ToolCallResult { Success = true, Output = """{"weather":"晴"}""" }),
        });

        var r = await RunToolSkill(skill, "用工具查上海天气");

        Assert.True(r.Sufficient);
        Assert.Contains("weather_now", r.Summary);
        Assert.Contains("上海今天晴", r.Draft);
        Assert.Equal(1, chat.Calls[0].ToolCount);   // 模型这一轮就看到了热注册的工具
    }

    [Fact]
    public async Task Planner_RoutesToolPhraseToToolsSkill()
    {
        var planner = new MemoryAssistant.Core.Agent.Planner.Planner(
            MemoryAssistant.Core.Agent.Planner.SkillCatalog.Default(), new AgentOptions(), chat: null);

        var plan = await planner.PlanAsync("调用工具 查一下上海天气", preferLlm: false);

        Assert.Contains(plan.Steps, s => s.Name == "tools");
    }
}
