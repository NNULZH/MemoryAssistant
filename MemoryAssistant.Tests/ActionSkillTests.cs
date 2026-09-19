using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Action;
using MemoryAssistant.Core.Agent.Planner;
using MemoryAssistant.Core.Agent.Skills;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.Integrations;

namespace MemoryAssistant.Tests;

/// <summary>V3.4 单测：写操作指令解析 / 人工确认闸门 / action Skill 行为（零 LLM / 零真实微信）。</summary>
public sealed class ActionSkillTests
{
    // ---------- 指令解析 ----------

    [Theory]
    [InlineData("打开张三的聊天", "open_chat", "张三", "")]
    [InlineData("打开和张三的聊天", "open_chat", "张三", "")]
    [InlineData("打开文件传输助手的聊天", "open_chat", "文件传输助手", "")]
    [InlineData("打开小李的会话", "open_chat", "小李", "")]
    [InlineData("给张三发消息：晚上见", "send_message", "张三", "晚上见")]
    [InlineData("给张三发消息:晚上见", "send_message", "张三", "晚上见")]
    [InlineData("给文件传输助手发消息：测试一下", "send_message", "文件传输助手", "测试一下")]
    [InlineData("回复：好的", "send_message", "", "好的")]
    [InlineData("回复他 好的", "send_message", "", "好的")]
    // 口语说法（用户实测最常这么讲）
    [InlineData("跟文件传输助手说一声：自测-C2", "send_message", "文件传输助手", "自测-C2")]
    [InlineData("对张三说：晚点到", "send_message", "张三", "晚点到")]
    [InlineData("告诉张三：晚点到", "send_message", "张三", "晚点到")]
    [InlineData("发消息给张三：晚点到", "send_message", "张三", "晚点到")]
    [InlineData("帮我给文件传输助手发消息，内容是自测-C3", "send_message", "文件传输助手", "自测-C3")]
    [InlineData("麻烦给张三发一条消息：明天见", "send_message", "张三", "明天见")]
    // 用户实测最自然的一种说法：目标在前、量词在后、正文直接跟在后面（无冒号）
    [InlineData("给张晓明发一条测试消息", "send_message", "张晓明", "测试消息")]
    // 规划器常把目标重述成"微信「X」"这类带壳写法，解析时要能剥掉（否则会拿假名字去搜索）
    [InlineData("打开与微信「文件传输助手」的会话", "open_chat", "文件传输助手", "")]
    public void Parser_ExtractsAction(string query, string action, string target, string payload)
    {
        var parsed = ActionIntentParser.TryParse(query);
        Assert.NotNull(parsed);
        Assert.Equal(action, parsed!.Action);
        Assert.Equal(target, parsed.Target);
        Assert.Equal(payload, parsed.Payload);
    }

    [Fact]
    public void Parser_OnlyOpen_WhenNoPayload()
    {
        var parsed = ActionIntentParser.TryParse("给张三发消息");
        Assert.NotNull(parsed);
        Assert.Equal("open_chat", parsed!.Action);
        Assert.Equal("张三", parsed.Target);
        Assert.Equal("", parsed.Payload);
    }

    [Theory]
    [InlineData("看看和张三的聊天")]      // 只读请求，不该被当成写操作
    [InlineData("我和张三聊过什么")]
    [InlineData("打开微信窗口")]           // 属只读"看屏幕"类
    [InlineData("回复率是多少")]           // 无冒号/代词 → 不误判
    [InlineData("你好")]
    // 陈述句必须挡住：放松口语说法后最容易误伤的几条（一旦误判就是"真的发错人"）
    [InlineData("我跟张晓明说过这件事")]     // 没有冒号 → 不能当成"给张晓明发送：过这件事"
    [InlineData("我和他讲了半天")]         // 同上
    [InlineData("我给张晓明发了个红包")]     // 没有冒号、也没有"消息"二字 → 不能当成发送
    [InlineData("给张晓明发了个红包")]       // 光杆"个"不算文本量词：红包发不了，更不能发出去一句"红包"
    [InlineData("他告诉我明天开会")]        // 没有冒号 → 不误判
    public void Parser_RejectsNonAction(string query)
        => Assert.Null(ActionIntentParser.TryParse(query));

    [Fact]
    public async Task Planner_RoutesWriteCommandToActionSkill()
    {
        var planner = new Planner(SkillCatalog.Default(), new AgentOptions(), chat: null);
        var plan = await planner.PlanAsync("给张三发消息：晚上见", preferLlm: false);
        Assert.Contains(plan.Steps, s => s.Kind == PlanStepKind.Skill && s.Name == "action");
    }

    // ---------- 确认闸门 ----------

    private sealed class FakeActionBridge : IWeChatActionBridge
    {
        public bool Available { get; init; } = true;
        public bool OpenOk { get; init; } = true;
        public bool SendOk { get; init; } = true;
        public List<string> Calls { get; } = [];
        /// <summary>每次 OpenChatAsync 收到的分区类型（用来验证 kind 真的传到了桥）。</summary>
        public List<ChatTargetKind> OpenKinds { get; } = [];

        public bool IsAvailable => Available;

        public Task<ActionResult> OpenChatAsync(
            string person, ChatTargetKind kind = ChatTargetKind.Auto, CancellationToken ct = default)
        {
            Calls.Add($"open:{person}");
            OpenKinds.Add(kind);
            return Task.FromResult(new ActionResult { Success = OpenOk, Detail = "opened", Error = OpenOk ? null : "open failed" });
        }

        public Task<ActionResult> SendMessageAsync(string text, CancellationToken ct = default)
        {
            Calls.Add($"send:{text}");
            return Task.FromResult(new ActionResult { Success = SendOk, Detail = "sent", Error = SendOk ? null : "send failed" });
        }

        /// <summary>唤醒窗口（第三阶段补充）：测试桥只回报可用性，不真的动窗口。</summary>
        public Task<ActionResult> WakeWindowAsync(CancellationToken ct = default)
        {
            Calls.Add("wake");
            return Task.FromResult(new ActionResult
            {
                Success = Available,
                Detail = Available ? "已把微信窗口唤到前台并激活" : "",
                Error = Available ? null : "未找到微信窗口",
            });
        }
    }

    private static SkillResult Run(
        IAgentSkill skill, string query) => skill.ExecuteAsync(new SkillRequest { Query = query }, CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void DeniedConfirmation_DoesNotTouchWeChat()
    {
        var bridge = new FakeActionBridge();
        var skill = new ActionSkill(bridge, new DenyAllActionConfirmation());

        var r = Run(skill, "给张三发消息：晚上见");

        Assert.True(r.Success);
        Assert.Empty(bridge.Calls);              // 关键：未确认 → 一个动作都不做
        Assert.Contains("取消", r.Draft);
    }

    [Fact]
    public void DefaultConfirmation_IsDenyAll()
    {
        var bridge = new FakeActionBridge();
        var r = Run(new ActionSkill(bridge), "打开张三的聊天");

        Assert.Empty(bridge.Calls);
        Assert.Contains("取消", r.Draft);
    }

    [Fact]
    public void ApprovedConfirmation_OpensThenSends()
    {
        var bridge = new FakeActionBridge();
        var audited = new List<string>();
        var skill = new ActionSkill(bridge, new AlwaysApproveActionConfirmation(), audited.Add);

        var r = Run(skill, "给张三发消息：晚上见");

        Assert.True(r.Sufficient);
        Assert.Equal(["open:张三", "send:晚上见"], bridge.Calls);
        Assert.Contains("已向「张三」发送：晚上见", r.Draft);
        Assert.Contains(audited, a => a.Contains("已执行"));
    }

    [Fact]
    public void ReplyWithoutTarget_OnlySends()
    {
        var bridge = new FakeActionBridge();
        var r = Run(new ActionSkill(bridge, new AlwaysApproveActionConfirmation()), "回复：好的");

        Assert.Equal(["send:好的"], bridge.Calls);
        Assert.Contains("当前会话", r.Draft);
    }

    [Fact]
    public void NoBridge_DegradesGracefully()
    {
        var r = Run(new ActionSkill(null, new AlwaysApproveActionConfirmation()), "打开张三的聊天");
        Assert.True(r.Success);
        Assert.False(r.Sufficient);
        Assert.Contains("未接入", r.Summary);
    }

    [Fact]
    public void BridgeUnavailable_ReportsHonestly()
    {
        var bridge = new FakeActionBridge { Available = false };
        var r = Run(new ActionSkill(bridge, new AlwaysApproveActionConfirmation()), "打开张三的聊天");
        Assert.True(r.Success);
        Assert.False(r.Sufficient);
        Assert.Contains("未找到微信窗口", r.Summary);
        Assert.Empty(bridge.Calls);
    }

    [Fact]
    public void OpenFailure_StopsBeforeSending()
    {
        var bridge = new FakeActionBridge { OpenOk = false };
        var r = Run(new ActionSkill(bridge, new AlwaysApproveActionConfirmation()), "给张三发消息：晚上见");

        Assert.False(r.Success);
        Assert.Equal(["open:张三"], bridge.Calls);   // 打开失败就不再发送，避免发错人
        Assert.Contains("打开", r.Error);
    }

    [Fact]
    public void UnparsableQuery_ExplainsSupportedPhrasings()
    {
        var r = Run(new ActionSkill(null, new AlwaysApproveActionConfirmation()), "随便说点什么");
        Assert.True(r.Success);
        Assert.False(r.Sufficient);
        Assert.Contains("未能识别", r.Summary);
    }

    // ---------- 写操作也要出现在「工具调用」台账里（否则用户只看到一句"我发好了"）----------
    // 第三阶段补充：确认本身也是一步**可见的**工具调用（request_user_confirmation），
    // 而且顺序是"先问用户 → 再看环境 → 才动手"。

    [Fact]
    public async Task ActionSkill_EmitsToolEventsForConfirmStatusOpenAndSend()
    {
        var bridge = new FakeActionBridge();
        var skill = new ActionSkill(bridge, new AlwaysApproveActionConfirmation());
        var events = new List<AgentProgress>();

        await skill.ExecuteAsync(
            new SkillRequest { Query = "给文件传输助手发消息：自测-D1", OnTool = events.Add },
            CancellationToken.None);

        Assert.All(events, e => Assert.Equal(AgentProgressKind.ToolCall, e.Kind));

        // 先问用户，再看微信在不在，最后才动手
        Assert.Equal(
            ["request_user_confirmation", "request_user_confirmation", "wechat_status", "wechat_status",
             "open_chat", "open_chat", "send_message", "send_message"],
            events.Select(e => e.ToolName));
        Assert.Equal(["start", "finish", "start", "finish", "start", "finish", "start", "finish"],
            events.Select(e => e.Phase));
        Assert.All(events.Where(e => e.Phase == "finish"), e => Assert.True(e.ToolSuccess));
        Assert.Contains("确认执行", events[1].ToolSummary);            // 用户的选择要能看到
        Assert.Contains("文件传输助手", events[4].ToolArgs);           // 打开的目标
        Assert.Contains("kind=", events[4].ToolArgs);                   // 分区也写在台账里（没接目录核对时是 auto）
        Assert.Contains("自测-D1", events[6].ToolArgs);                // 发出去的内容也要看得到
    }

    [Fact]
    public async Task ActionSkill_OpenFailure_EmitsFailedEventAndNoSendEvent()
    {
        var bridge = new FakeActionBridge { OpenOk = false };
        var skill = new ActionSkill(bridge, new AlwaysApproveActionConfirmation());
        var events = new List<AgentProgress>();

        await skill.ExecuteAsync(
            new SkillRequest { Query = "给张三发消息：晚上见", OnTool = events.Add },
            CancellationToken.None);

        // 确认(2) + 环境检查(2) + 打开(2)，打开那一步失败
        Assert.Equal(6, events.Count);
        Assert.Equal("open_chat", events[4].ToolName);
        Assert.Equal("finish", events[5].Phase);
        Assert.False(events[5].ToolSuccess);
        Assert.DoesNotContain(events, e => e.ToolName == "send_message");
    }

    [Fact]
    public async Task ActionSkill_DeniedConfirmation_EmitsOnlyConfirmationEvent()
    {
        var bridge = new FakeActionBridge();
        var skill = new ActionSkill(bridge, new DenyAllActionConfirmation());
        var events = new List<AgentProgress>();

        await skill.ExecuteAsync(
            new SkillRequest { Query = "给张三发消息：晚上见", OnTool = events.Add },
            CancellationToken.None);

        // 被拒绝时只留"问过用户"这一条记录（它是真实发生过的），其余一个都不该有
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal("request_user_confirmation", e.ToolName));
        Assert.Contains("取消", events[1].ToolSummary);
        Assert.Empty(bridge.Calls);
    }

    // ---------- 同一套写操作，作为 Agent 可调用的工具（真实 JSON Schema + 同样走确认闸门）----------

    private static ToolRegistry Registry(IWeChatActionBridge bridge, IActionConfirmation confirmation)
    {
        var registry = new ToolRegistry();
        new MemoryAssistant.Infrastructure.Agent.WeChatActionToolProvider(bridge, confirmation).RegisterAll(registry);
        return registry;
    }

    private static Task<ToolCallResult> Call(ToolRegistry registry, string name, string argsJson)
    {
        Assert.True(registry.TryGet(name, out var tool));
        return tool.ExecuteAsync(argsJson, CancellationToken.None);
    }

    [Fact]
    public void Tools_ExposeRealSchema_AsActionCategory()
    {
        var registry = Registry(new FakeActionBridge(), new DenyAllActionConfirmation());
        var schemas = registry.BuildSchemas();

        Assert.Equal(2, schemas.Count);

        var open = schemas.Single(s => s.Name == "wechat_open_chat");
        Assert.StartsWith("[Action]", open.Description);
        Assert.Equal("string", open.Parameters!["properties"]!["person"]!["type"]!.GetValue<string>());
        Assert.Contains("person", open.Parameters["required"]!.AsArray().Select(n => n!.GetValue<string>()));

        var send = schemas.Single(s => s.Name == "wechat_send_message");
        Assert.Contains("text", send.Parameters!["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(registry.All.Single(t => t.Name == "wechat_send_message").ReadOnly);
    }

    [Fact]
    public async Task Tool_DeniedConfirmation_DoesNotTouchWeChat()
    {
        var bridge = new FakeActionBridge();
        var result = await Call(Registry(bridge, new DenyAllActionConfirmation()),
            "wechat_send_message", """{"text":"你好"}""");

        Assert.False(result.Success);
        Assert.Contains("取消", result.Error);
        Assert.Empty(bridge.Calls);
    }

    [Fact]
    public async Task Tool_Approved_OpensAndSends()
    {
        var bridge = new FakeActionBridge();
        var registry = Registry(bridge, new AlwaysApproveActionConfirmation());

        var open = await Call(registry, "wechat_open_chat", """{"person":"张三"}""");
        var send = await Call(registry, "wechat_send_message", """{"text":"晚上见"}""");

        Assert.True(open.Success);
        Assert.True(send.Success);
        Assert.Equal(["open:张三", "send:晚上见"], bridge.Calls);
    }

    [Fact]
    public async Task Tool_RejectsNewlinePayload()
    {
        var bridge = new FakeActionBridge();
        var result = await Call(Registry(bridge, new AlwaysApproveActionConfirmation()),
            "wechat_send_message", """{"text":"第一行\n第二行"}""");

        Assert.False(result.Success);
        Assert.Contains("换行", result.Error);
        Assert.Empty(bridge.Calls);
    }

    [Fact]
    public async Task Tool_EmptyPerson_IsRejectedBeforeConfirm()
    {
        var bridge = new FakeActionBridge();
        var result = await Call(Registry(bridge, new AlwaysApproveActionConfirmation()),
            "wechat_open_chat", """{"person":"  "}""");

        Assert.False(result.Success);
        Assert.Empty(bridge.Calls);
    }

    // ---------- kind：搜索结果下拉是分区的，填错分区就会点进别的会话 ----------

    [Fact]
    public void Tools_SchemaExposesKindEnum()
    {
        var schemas = Registry(new FakeActionBridge(), new DenyAllActionConfirmation()).BuildSchemas();

        foreach (var name in new[] { "wechat_open_chat", "wechat_send_message" })
        {
            var kind = schemas.Single(s => s.Name == name).Parameters!["properties"]!["kind"]!;
            Assert.Equal("string", kind["type"]!.GetValue<string>());
            Assert.Equal(["contact", "group", "auto"],
                kind["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
        }
    }

    [Theory]
    [InlineData("""{"text":"晚上见","person":"张三","kind":"contact"}""", ChatTargetKind.Contact)]
    [InlineData("""{"text":"晚上见","person":"张三","kind":"group"}""", ChatTargetKind.Group)]
    [InlineData("""{"text":"晚上见","person":"张三","kind":"Group"}""", ChatTargetKind.Group)]  // 大小写不敏感
    [InlineData("""{"text":"晚上见","person":"张三"}""", ChatTargetKind.Auto)]                 // 不给 → auto
    [InlineData("""{"text":"晚上见","person":"张三","kind":"???"}""", ChatTargetKind.Auto)]    // 认不出不乱猜
    public async Task Tool_PassesKindThroughToBridge(string args, ChatTargetKind expected)
    {
        var bridge = new FakeActionBridge();
        var result = await Call(Registry(bridge, new AlwaysApproveActionConfirmation()), "wechat_send_message", args);

        Assert.True(result.Success);
        Assert.Equal(expected, Assert.Single(bridge.OpenKinds));
    }

    [Fact]
    public async Task ActionSkill_UsesAutoKind()
    {
        // 规则路径认不出联系人/群聊（没有会话目录），必须是 Auto——不能瞎填一个分区
        var bridge = new FakeActionBridge();
        await new ActionSkill(bridge, new AlwaysApproveActionConfirmation())
            .ExecuteAsync(new SkillRequest { Query = "给张三发消息：晚上见" }, CancellationToken.None);

        Assert.Equal(ChatTargetKind.Auto, Assert.Single(bridge.OpenKinds));
    }
}
