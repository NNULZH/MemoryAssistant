using System.Text.Json;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.Integrations;
using MemoryAssistant.Core.Missions;
using MemoryAssistant.Core.PythonBridge;
using MemoryAssistant.Infrastructure.Missions;

namespace MemoryAssistant.Tests;

/// <summary>
/// V3.7 自动回复单测：意图解析 + 子智能体（抓新消息→生成→发送）+ 全部"不发"的安全分支。
/// 全程零 LLM、零真实微信、零 Bridge 进程（用 fake 驱动）。
/// </summary>
public sealed class AutoReplyTests
{
    private const string Target = "张晓明";
    private const string SessionId = "wxid_peer";

    // ---------- 意图解析（对话里怎么把话变成自动回复任务） ----------

    [Fact]
    public void Intent_AutoReplyWithTarget_BecomesWatchAutoReplyMission()
    {
        var draft = MissionIntentParser.TryParse("帮我自动回复张晓明的消息");

        Assert.NotNull(draft);
        Assert.Equal(MissionActionKind.AutoReply, draft!.Action);
        Assert.Equal(MissionTriggerKind.Watch, draft.Trigger);   // 默认"增量激活"，不是定时轮询
        Assert.Equal("张晓明", draft.Target);
        Assert.Contains("自动回复", draft.Title);
        Assert.Contains("自动回复", draft.TriggerText);
        Assert.Contains("自动发送", draft.Reason);                // 必须让用户在创建前看见这是写操作
    }

    [Fact]
    public void Intent_TargetBeforeVerb_IsExtracted()
    {
        var draft = MissionIntentParser.TryParse("张晓明发消息就自动回复");

        Assert.NotNull(draft);
        Assert.Equal(MissionActionKind.AutoReply, draft!.Action);
        Assert.Equal("张晓明", draft.Target);
    }

    [Fact]
    public void Intent_WithScheduleWord_BecomesIntervalAutoReply()
    {
        var draft = MissionIntentParser.TryParse("每天自动回复张晓明的消息");

        Assert.NotNull(draft);
        Assert.Equal(MissionActionKind.AutoReply, draft!.Action);
        Assert.Equal(MissionTriggerKind.Interval, draft.Trigger);  // 写了周期词 → 定时扫描
        Assert.Equal(1440, draft.IntervalMinutes);
        Assert.Equal("张晓明", draft.Target);
    }

    [Theory]
    [InlineData("帮我自动回复一下吧")]                 // 说不出回给谁
    [InlineData("怎么自动回复别人的消息？")]            // 疑问句
    [InlineData("自动回复")]                           // 没有对象
    public void Intent_WithoutUsableTarget_IsNotAMission(string query)
    {
        Assert.Null(MissionIntentParser.TryParse(query));
    }

    [Fact]
    public void Intent_PlainTrackMission_StaysSummarize()
    {
        var draft = MissionIntentParser.TryParse("帮我追踪和张晓明的聊天，有新消息就总结给我");

        Assert.NotNull(draft);
        Assert.Equal(MissionActionKind.Summarize, draft!.Action);   // 回归：不要把所有追踪都变成自动回复
        Assert.Equal(MissionTriggerKind.Watch, draft.Trigger);
        Assert.Equal("张晓明", draft.Target);
    }

    [Fact]
    public void Intent_ToDefinition_KeepsActionAndTarget()
    {
        var draft = MissionIntentParser.TryParse("每天自动回复张晓明的消息")!;
        var def = draft.ToDefinition();

        Assert.Equal(MissionActionKind.AutoReply, def.Action);
        Assert.Equal("张晓明", def.Target);        // Interval 型也要保留对象，否则不知道回给谁
        Assert.Contains("自动回复", def.TriggerText);
        Assert.True(def.RequiresApproval);         // 创建那一刻的人工确认仍然要求
    }

    [Fact]
    public void Repository_RoundTripsAction()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missions_ar_{Guid.NewGuid():N}.json");
        try
        {
            var repo = new JsonMissionRepository(path);
            var store = new MissionStore();
            store.Add(new MissionDefinition
            {
                Title = "自动回复",
                Goal = "帮我自动回复张晓明的消息",
                Trigger = MissionTriggerKind.Watch,
                Action = MissionActionKind.AutoReply,
                Target = Target,
            });
            repo.Save(store.Items);

            var loaded = Assert.Single(repo.Load());
            Assert.Equal(MissionActionKind.AutoReply, loaded.Action);
            Assert.Equal(Target, loaded.Target);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---------- 子智能体：该发的发出去 ----------

    [Fact]
    public async Task SubAgent_WithNewIncomingMessage_SendsCleanedReply()
    {
        var now = DateTimeOffset.Now.ToUnixTimeSeconds();
        var bridge = Bridge(new[]
        {
            (now - 3600, false, Target, "在忙吗"),
            (now - 60, true, "我", "刚忙完"),
            (now + 120, false, Target, "晚上一起吃饭？"),
        });
        var action = new FakeActionBridge();
        var chat = new ScriptedChatClient([new ChatResult { Content = "\"好呀，几点？\"" }]);
        var mission = AutoReplyMission(lastRunAt: DateTimeOffset.Now);

        var outcome = await SubAgent(bridge, chat, action).RunAsync(mission, CancellationToken.None);

        Assert.True(outcome.Sent);
        Assert.Equal(1, outcome.NewCount);                                  // 只算对方发来的那条
        Assert.Equal(new[] { "好呀，几点？" }, action.Sent);                // 引号已清掉
        Assert.Equal(new[] { Target }, action.Opened);                      // 先打开目标会话再发
        Assert.Equal(new[] { ChatTargetKind.Contact }, action.OpenedKinds);  // 私聊要声明「联系人」分区
        Assert.Contains("已自动回复", outcome.Note);
        Assert.Contains("好呀，几点？", outcome.Note);                       // 关键信息里带回复正文
    }

    [Fact]
    public async Task SubAgent_GroupTarget_OpensAsGroup()
    {
        // 群聊回复必须声明 kind=Group：搜索下拉里「联系人」分区排在前面，
        // 不声明就可能在"联系人"里命中一个同名的人，把回复发错对象。
        var now = DateTimeOffset.Now.ToUnixTimeSeconds();
        var bridge = new FakeBridge
        {
            Responder = (method, args) => method switch
            {
                // 子智能体必须用 find_sessions 找会话（list_sessions 的关键词只匹配 wxid/摘要/最后发言人，
                // 会把"张晓明"匹配到她发言过的群）。假桥按真实返回带上 display_name。
                "find_sessions" => "[{\"username\":\"49918014043@chatroom\",\"display_name\":\"食堂预定群\"}]",
                "read_messages" => MessagesJson([(now + 60, false, "群友", "在吗")]),
                _ => "[]",
            },
        };
        var action = new FakeActionBridge();
        var mission = AutoReplyMission(lastRunAt: DateTimeOffset.Now, target: "食堂预定群");

        var outcome = await SubAgent(bridge, new ScriptedChatClient([new ChatResult { Content = "在的" }]), action)
            .RunAsync(mission, CancellationToken.None);

        Assert.True(outcome.Sent);
        Assert.Equal(new[] { ChatTargetKind.Group }, action.OpenedKinds);
    }

    [Fact]
    public async Task SubAgent_TargetAlias_IsUsedForLookupButRealNameForSending()
    {
        var now = DateTimeOffset.Now.ToUnixTimeSeconds();
        var bridge = new FakeBridge
        {
            Responder = (method, args) => method switch
            {
                // 别名解析出英文 id（filehelper），靠 username 精确命中
                "find_sessions" => "[{\"username\":\"filehelper\",\"display_name\":\"文件传输助手\"}]",
                "read_messages" => MessagesJson([(now + 60, false, "文件传输助手", "测试")]),
                _ => "[]",
            },
        };
        var action = new FakeActionBridge();
        var mission = AutoReplyMission(lastRunAt: DateTimeOffset.Now, target: "文件传输助手");

        var outcome = await SubAgent(bridge, new ScriptedChatClient([new ChatResult { Content = "收到" }]), action)
            .RunAsync(mission, CancellationToken.None);

        Assert.True(outcome.Sent);
        Assert.Contains("filehelper", string.Join(",", bridge.Calls.Select(c => c.Args)));  // 库里按 id 找
        Assert.Equal(new[] { "文件传输助手" }, action.Opened);                              // 发送按人话名去找会话
    }

    // ---------- 子智能体：不该发的一条都不发 ----------

    [Fact]
    public async Task SubAgent_FirstRun_OnlyRecordsBaseline()
    {
        var now = DateTimeOffset.Now.ToUnixTimeSeconds();
        var bridge = Bridge([(now + 120, false, Target, "在吗")]);
        var action = new FakeActionBridge();
        // LastRunAt 为空 = 首次运行：绝不能把历史消息回一遍
        var mission = AutoReplyMission(lastRunAt: null);

        var outcome = await SubAgent(bridge, new ScriptedChatClient([new ChatResult { Content = "在的" }]), action)
            .RunAsync(mission, CancellationToken.None);

        Assert.False(outcome.Sent);
        Assert.True(outcome.Skipped);
        Assert.Contains("基线", outcome.Note);
        Assert.Empty(action.Opened);
        Assert.Empty(action.Sent);
    }

    [Fact]
    public async Task SubAgent_NoNewMessages_DoesNotSend()
    {
        var now = DateTimeOffset.Now.ToUnixTimeSeconds();
        var bridge = Bridge([(now - 3600, false, Target, "旧消息")]);
        var action = new FakeActionBridge();

        var outcome = await SubAgent(bridge, new ScriptedChatClient([new ChatResult { Content = "在的" }]), action)
            .RunAsync(AutoReplyMission(DateTimeOffset.Now), CancellationToken.None);

        Assert.False(outcome.Sent);
        Assert.Contains("没有对方的新消息", outcome.Note);
        Assert.Empty(action.Opened);
    }

    [Fact]
    public async Task SubAgent_OnlySelfOrNoise_DoesNotSend()
    {
        var now = DateTimeOffset.Now.ToUnixTimeSeconds();
        var bridge = Bridge(
        [
            (now + 60, true, "我", "我自己刚发的"),
            (now + 61, false, Target, "[图片]"),                     // 纯媒体
            (now + 62, false, Target, "我通过了你的朋友验证请求"),     // 系统噪音
        ]);
        var action = new FakeActionBridge();

        var outcome = await SubAgent(bridge, new ScriptedChatClient([new ChatResult { Content = "好" }]), action)
            .RunAsync(AutoReplyMission(DateTimeOffset.Now), CancellationToken.None);

        Assert.False(outcome.Sent);
        Assert.Empty(action.Opened);
    }

    [Fact]
    public async Task SubAgent_ModelSaysSkip_DoesNotSend()
    {
        var bridge = Bridge([(DateTimeOffset.Now.ToUnixTimeSeconds() + 60, false, Target, "广告：点击领取")]);
        var action = new FakeActionBridge();

        var outcome = await SubAgent(bridge, new ScriptedChatClient([new ChatResult { Content = "SKIP" }]), action)
            .RunAsync(AutoReplyMission(DateTimeOffset.Now), CancellationToken.None);

        Assert.False(outcome.Sent);
        Assert.Empty(action.Opened);
        Assert.Contains("未发送", outcome.Note);
    }

    [Fact]
    public async Task SubAgent_ModelFailure_DoesNotSend()
    {
        var bridge = Bridge([(DateTimeOffset.Now.ToUnixTimeSeconds() + 60, false, Target, "在吗")]);
        var action = new FakeActionBridge();

        var outcome = await SubAgent(bridge, new ThrowingChatClient(), action)
            .RunAsync(AutoReplyMission(DateTimeOffset.Now), CancellationToken.None);

        Assert.False(outcome.Sent);
        Assert.Empty(action.Sent);
        Assert.Null(outcome.Error);            // 生成失败不是"执行失败"，但要如实说没发
    }

    [Fact]
    public async Task SubAgent_OpenChatFails_NeverSendsToUnknownFocus()
    {
        var bridge = Bridge([(DateTimeOffset.Now.ToUnixTimeSeconds() + 60, false, Target, "在吗")]);
        var action = new FakeActionBridge { OpenSucceeds = false };

        var outcome = await SubAgent(bridge, new ScriptedChatClient([new ChatResult { Content = "在的" }]), action)
            .RunAsync(AutoReplyMission(DateTimeOffset.Now), CancellationToken.None);

        Assert.False(outcome.Sent);
        Assert.Empty(action.Sent);                                    // 关键安全断言：打开失败绝不发送
        Assert.NotNull(outcome.Error);
        Assert.Contains("未发送", outcome.Note);
    }

    [Fact]
    public async Task SubAgent_SendFails_ReportsHonestly()
    {
        var bridge = Bridge([(DateTimeOffset.Now.ToUnixTimeSeconds() + 60, false, Target, "在吗")]);
        var action = new FakeActionBridge { SendSucceeds = false };

        var outcome = await SubAgent(bridge, new ScriptedChatClient([new ChatResult { Content = "在的" }]), action)
            .RunAsync(AutoReplyMission(DateTimeOffset.Now), CancellationToken.None);

        Assert.False(outcome.Sent);
        Assert.NotNull(outcome.Error);
        Assert.Contains("发送失败", outcome.Note);
    }

    [Fact]
    public async Task SubAgent_NoTarget_DoesNotEvenReadOrSend()
    {
        var bridge = Bridge([(DateTimeOffset.Now.ToUnixTimeSeconds() + 60, false, Target, "在吗")]);
        var action = new FakeActionBridge();

        var outcome = await SubAgent(bridge, new ScriptedChatClient([new ChatResult { Content = "在的" }]), action)
            .RunAsync(AutoReplyMission(DateTimeOffset.Now, target: ""), CancellationToken.None);

        Assert.False(outcome.Sent);
        Assert.Empty(bridge.Calls);          // 连读都不读
        Assert.Empty(action.Opened);
        Assert.Contains("未指定回复对象", outcome.Note);
    }

    [Fact]
    public async Task SubAgent_WindowUnavailable_DoesNotSend()
    {
        var bridge = Bridge([(DateTimeOffset.Now.ToUnixTimeSeconds() + 60, false, Target, "在吗")]);
        var action = new FakeActionBridge { Available = false };

        var outcome = await SubAgent(bridge, new ScriptedChatClient([new ChatResult { Content = "在的" }]), action)
            .RunAsync(AutoReplyMission(DateTimeOffset.Now), CancellationToken.None);

        Assert.False(outcome.Sent);
        Assert.Empty(action.Opened);
        Assert.Contains("未找到微信窗口", outcome.Note);
    }

    [Fact]
    public async Task SubAgent_NoActionBridge_DoesNotSend()
    {
        var bridge = Bridge([(DateTimeOffset.Now.ToUnixTimeSeconds() + 60, false, Target, "在吗")]);

        var outcome = await SubAgent(bridge, new ScriptedChatClient([new ChatResult { Content = "在的" }]), null)
            .RunAsync(AutoReplyMission(DateTimeOffset.Now), CancellationToken.None);

        Assert.False(outcome.Sent);
        Assert.Contains("写操作未接入", outcome.Note);
    }

    [Fact]
    public async Task SubAgent_NoChatClient_DoesNotSend()
    {
        var bridge = Bridge([(DateTimeOffset.Now.ToUnixTimeSeconds() + 60, false, Target, "在吗")]);
        var action = new FakeActionBridge();

        var outcome = await SubAgent(bridge, null, action)
            .RunAsync(AutoReplyMission(DateTimeOffset.Now), CancellationToken.None);

        Assert.False(outcome.Sent);
        Assert.Contains("未配置模型", outcome.Note);
        Assert.Empty(action.Sent);
    }

    [Fact]
    public async Task SubAgent_EcoMode_OnlyProducesDraft()
    {
        var bridge = Bridge([(DateTimeOffset.Now.ToUnixTimeSeconds() + 60, false, Target, "在吗")]);
        var action = new FakeActionBridge();

        var outcome = await SubAgent(bridge, new ScriptedChatClient([new ChatResult { Content = "在的" }]), action,
            eco: true).RunAsync(AutoReplyMission(DateTimeOffset.Now), CancellationToken.None);

        Assert.False(outcome.Sent);
        Assert.True(outcome.Generated);
        Assert.Equal("在的", outcome.Draft);
        Assert.Empty(action.Sent);
        Assert.Contains("Eco", outcome.Note);
    }

    [Fact]
    public async Task SubAgent_GlobalBrake_OnlyProducesDraft()
    {
        var bridge = Bridge([(DateTimeOffset.Now.ToUnixTimeSeconds() + 60, false, Target, "在吗")]);
        var action = new FakeActionBridge();

        var outcome = await SubAgent(bridge, new ScriptedChatClient([new ChatResult { Content = "在的" }]), action,
            suppress: true).RunAsync(AutoReplyMission(DateTimeOffset.Now), CancellationToken.None);

        Assert.False(outcome.Sent);
        Assert.Equal("在的", outcome.Draft);
        Assert.Empty(action.Opened);
        Assert.Contains("全局关闭", outcome.Note);
    }

    [Fact]
    public async Task SubAgent_BridgeFailure_DoesNotSend()
    {
        var bridge = new FakeBridge { Responder = (_, _) => throw new InvalidOperationException("bridge 断了") };
        var action = new FakeActionBridge();
        var mission = AutoReplyMission(DateTimeOffset.Now);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SubAgent(bridge, new ScriptedChatClient([new ChatResult { Content = "在的" }]), action)
                .RunAsync(mission, CancellationToken.None));
        Assert.Empty(action.Sent);
    }

    // ---------- 回复正文清洗 ----------

    [Theory]
    [InlineData("回复：好的", "好的")]
    [InlineData("\"好呀\"", "好呀")]
    [InlineData("第一行\n第二行", "第一行 第二行")]
    [InlineData("  好的  ", "好的")]
    [InlineData("SKIP", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void CleanReply_NormalizesModelOutput(string raw, string? expected)
        => Assert.Equal(expected, AutoReplySubAgent.CleanReply(raw));

    [Fact]
    public void CleanReply_OverlongText_IsTruncated()
    {
        var raw = new string('好', 500);
        var cleaned = AutoReplySubAgent.CleanReply(raw);

        Assert.NotNull(cleaned);
        Assert.Equal(200, cleaned!.Length);
        Assert.DoesNotContain('\n', cleaned);
    }

    // ---------- 调度接入：增量激活 → 子智能体 → 发送 ----------

    [Fact]
    public async Task Scheduler_WatchAutoReply_ProbeGatesTheSend()
    {
        var now = DateTimeOffset.Now.ToUnixTimeSeconds();
        var store = new MissionStore();
        var mission = store.Add(new MissionDefinition
        {
            Title = "自动回复",
            Goal = "帮我自动回复张晓明的消息",
            Trigger = MissionTriggerKind.Watch,
            Action = MissionActionKind.AutoReply,
            Target = Target,
        });
        mission.RecordRun("基线");

        var bridge = Bridge([(now + 60, false, Target, "在吗")]);
        var action = new FakeActionBridge();
        var subAgent = SubAgent(bridge, new ScriptedChatClient([new ChatResult { Content = "在的" }]), action);

        var probeResult = MissionProbeResult.Skip("目标会话没有对方的新消息");
        var scheduler = new MissionScheduler(
            store, new SubAgentExecutor(subAgent), probe: new FakeProbe(() => probeResult));

        await scheduler.RunOnceAsync(mission.Id);
        Assert.Empty(action.Sent);                          // 探测说没新消息 → 子智能体根本不跑

        probeResult = MissionProbeResult.Run("新增 1 条", 1, "[09:00] 张晓明: 在吗");
        await scheduler.RunOnceAsync(mission.Id);

        Assert.Single(action.Sent);                 // 有新消息 → 真的发出去
        Assert.Contains("已自动回复", mission.LastResult);   // 只把关键信息落进任务"上次结果"
    }

    [Fact]
    public void Scheduler_AutoReplyTask_IsSchedulable()
    {
        var store = new MissionStore();
        var m = store.Add(new MissionDefinition
        {
            Title = "自动回复",
            Goal = "g",
            Trigger = MissionTriggerKind.Watch,
            Action = MissionActionKind.AutoReply,
            Target = Target,
        });
        var scheduler = new MissionScheduler(store, new SubAgentExecutor(null));

        Assert.True(scheduler.StartMission(m.Id));   // 追踪型可以纳管调度
        Assert.Equal(MissionStatus.Running, m.Status);
    }

    // ---------- fakes / helpers ----------

    private sealed class FakeProbe(Func<MissionProbeResult> result) : IMissionProbe
    {
        public Task<MissionProbeResult> ProbeAsync(MissionDefinition mission, CancellationToken ct)
            => Task.FromResult(result());
    }

    private sealed class SubAgentExecutor(AutoReplySubAgent? agent) : IMissionExecutor
    {
        public Task<MissionExecutionResult> ExecuteAsync(
            MissionDefinition mission, MissionProbeResult? probe, CancellationToken ct)
            => agent is null
                ? Task.FromResult(new MissionExecutionResult { Success = true, Summary = "noop" })
                : RunAsync(agent, mission, ct);

        private static async Task<MissionExecutionResult> RunAsync(
            AutoReplySubAgent agent, MissionDefinition mission, CancellationToken ct)
        {
            var o = await agent.RunAsync(mission, ct);
            return new MissionExecutionResult
            {
                Success = o.Error is null,
                Summary = o.Note,
                EvidenceCount = o.NewCount,
                Skipped = !o.Sent,
            };
        }
    }

    private sealed class FakeBridge : IPythonBridge
    {
        public Func<string, IReadOnlyDictionary<string, object?>?, string> Responder { get; set; } = (_, _) => "[]";
        public List<(string Method, string Args)> Calls { get; } = [];

        public bool IsRunning => true;
        public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task StopAsync() => Task.CompletedTask;

        public Task<BridgeResponse> RequestAsync(
            string method, IReadOnlyDictionary<string, object?>? args = null,
            int? timeoutSeconds = null, CancellationToken ct = default)
        {
            Calls.Add((method, string.Join(",", (args ?? new Dictionary<string, object?>())
                .Select(kv => kv.Key + "=" + kv.Value))));
            var json = Responder(method, args);
            using var doc = JsonDocument.Parse(json);
            return Task.FromResult(new BridgeResponse("1", true, doc.RootElement.Clone(), null));
        }

        public void Dispose() { }
    }

    private sealed class FakeActionBridge : IWeChatActionBridge
    {
        public bool Available { get; set; } = true;
        public bool OpenSucceeds { get; set; } = true;
        public bool SendSucceeds { get; set; } = true;
        public List<string> Opened { get; } = [];
        public List<string> Sent { get; } = [];
        /// <summary>打开会话时声明的分区类型（群聊回复必须传 Group，否则搜索会先命中同名的联系人）。</summary>
        public List<ChatTargetKind> OpenedKinds { get; } = [];

        public bool IsAvailable => Available;

        public Task<ActionResult> OpenChatAsync(
            string person, ChatTargetKind kind = ChatTargetKind.Auto, CancellationToken ct = default)
        {
            Opened.Add(person);
            OpenedKinds.Add(kind);
            return Task.FromResult(OpenSucceeds
                ? new ActionResult { Success = true, Detail = "已打开" }
                : new ActionResult { Success = false, Error = "窗口标题与目标不一致" });
        }

        public Task<ActionResult> SendMessageAsync(string text, CancellationToken ct = default)
        {
            Sent.Add(text);
            return Task.FromResult(SendSucceeds
                ? new ActionResult { Success = true, Detail = "已发送" }
                : new ActionResult { Success = false, Error = "回验未通过" });
        }

        /// <summary>唤醒窗口（第三阶段补充）：测试桥只回报可用性。</summary>
        public Task<ActionResult> WakeWindowAsync(CancellationToken ct = default)
            => Task.FromResult(Available
                ? new ActionResult { Success = true, Detail = "已唤醒" }
                : new ActionResult { Success = false, Error = "未找到微信窗口" });
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public string ModelName => "boom";
        public Task<ChatResult> ChatAsync(
            IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolSchema>? tools = null,
            CancellationToken ct = default)
            => Task.FromException<ChatResult>(new InvalidOperationException("模型不可用"));
    }

    private static MissionDefinition AutoReplyMission(DateTimeOffset? lastRunAt, string target = Target)
    {
        var m = new MissionDefinition
        {
            Title = "自动回复",
            Goal = "帮我自动回复她的消息",
            Trigger = MissionTriggerKind.Watch,
            Action = MissionActionKind.AutoReply,
            Target = target,
        };
        if (lastRunAt is not null) m.RecordRun("基线");
        return m;
    }

    private static AutoReplySubAgent SubAgent(
        FakeBridge bridge, IChatClient? chat, FakeActionBridge? action,
        bool eco = false, bool suppress = false)
        => new(bridge, chat, action, new AgentOptions { EcoMode = eco, SuppressAutoReply = suppress });

    private static FakeBridge Bridge(IEnumerable<(long Ts, bool Self, string Who, string Text)> messages)
        => new()
        {
            Responder = (method, _) => method switch
            {
                "find_sessions" => $"[{{\"username\":\"{SessionId}\",\"display_name\":\"{Target}\"}}]",
                "read_messages" => MessagesJson(messages),
                _ => "[]",
            },
        };

    private static string MessagesJson(IEnumerable<(long Ts, bool Self, string Who, string Text)> items)
        => JsonSerializer.Serialize(items.Select(i => new
        {
            create_time = i.Ts,
            is_self = i.Self,
            display_name = i.Who,
            content = i.Text,
        }));
}
