using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Action;
using MemoryAssistant.Core.Agent.Interaction;
using MemoryAssistant.Core.Agent.Skills;
using MemoryAssistant.Core.Integrations;

namespace MemoryAssistant.Tests;

/// <summary>
/// 第三阶段补充（强规则·对象核对）：写操作动手之前必须先查本地会话目录。
///
/// 背景（实测踩过）：规则路径把"给X发消息"直接交给 action Skill，那条路上只有 OCR + 模拟键鼠，
/// 看不到聊天库——于是"名字没打进搜索框"被说成"没有这个人"，私聊/群聊也只能靠 auto 瞎试。
/// 这组测试钉住的口径：
///   ① 目录里有 → 用目录里的名字，并按它对分区判私聊/群聊；
///   ② 目录里没有 → **不许据此断定"没有这个人"**，照原样继续；
///   ③ 目录查不动 → 明确标注"未核对"，绝不把"没查到"说成"不存在"。
/// </summary>
public sealed class TargetResolutionTests
{
    private sealed class FakeDirectory(params SessionHit[] hits) : ISessionDirectory
    {
        public List<string> Queries { get; } = [];
        public Func<string, IReadOnlyList<SessionHit>>? Responder { get; set; }
        public bool Throws { get; set; }

        public Task<IReadOnlyList<SessionHit>> FindAsync(string keyword, CancellationToken ct)
        {
            Queries.Add(keyword);
            if (Throws) throw new InvalidOperationException("目录挂了");
            return Task.FromResult(Responder?.Invoke(keyword) ?? (IReadOnlyList<SessionHit>)hits);
        }
    }

    private static SessionHit Hit(string id, string name, bool isGroup = false, long ts = 0)
        => new(id, name, isGroup, ts);

    // ---------- 核对器本身 ----------

    [Fact]
    public async Task Resolve_ExactNameWins_SoRecentButUnrelatedHitIsNotPicked()
    {
        // ⚠ "最近活跃"不能当首选判据：同一个词横跨多个会话，"张晓明的群"更活跃也不该顶掉本人
        var dir = new FakeDirectory(Hit("g1", "张晓明的群", isGroup: true, ts: 9_999), Hit("c1", "张晓明", ts: 100));
        var r = await new WeChatTargetResolver(dir).ResolveAsync("张晓明", CancellationToken.None);

        Assert.True(r.Found);
        Assert.Equal("张晓明", r.Name);
        Assert.False(r.IsGroup);
    }

    [Fact]
    public async Task Resolve_UsesDirectoryNameForSearch()
    {
        // 用户口语叫"紫薯"，聊天库里是"紫薯马国敬"：搜索要用库里的名字才容易命中
        var dir = new FakeDirectory(Hit("c1", "紫薯马国敬", ts: 5));
        var r = await new WeChatTargetResolver(dir).ResolveAsync("紫薯", CancellationToken.None);

        Assert.True(r.Found);
        Assert.Equal("紫薯", r.Query);
        Assert.Equal("紫薯马国敬", r.SearchName);
        Assert.Contains("紫薯马国敬", r.Note);
    }

    [Fact]
    public async Task Resolve_NotFound_IsMarkedCheckedButNotClaimedAsMissingPerson()
    {
        var r = await new WeChatTargetResolver(new FakeDirectory()).ResolveAsync("查无此人", CancellationToken.None);

        Assert.True(r.Checked);
        Assert.False(r.Found);
        Assert.Contains("不据此断定", r.Note);          // 只陈述"目录里没匹配到"，不下"人不存在"的结论
        Assert.Equal("查无此人", r.SearchName);         // 仍按原名字继续试
    }

    [Fact]
    public async Task Resolve_WithoutDirectory_IsMarkedUnchecked()
    {
        var r = await new WeChatTargetResolver().ResolveAsync("周斌", CancellationToken.None);

        Assert.False(r.Checked);
        Assert.False(r.Found);
        Assert.Contains("未接入", r.Note);
    }

    [Fact]
    public async Task Resolve_DirectoryFailure_DegradesInsteadOfThrowing()
    {
        var r = await new WeChatTargetResolver(new FakeDirectory { Throws = true })
            .ResolveAsync("周斌", CancellationToken.None);

        Assert.False(r.Checked);
        Assert.Contains("出错", r.Note);
    }

    // ---------- action Skill：先核对，再动手 ----------

    private sealed class RecordingBridge : IWeChatActionBridge
    {
        public bool OpenOk { get; init; } = true;
        public List<string> Calls { get; } = [];
        public List<ChatTargetKind> OpenKinds { get; } = [];
        public bool IsAvailable => true;

        public Task<ActionResult> OpenChatAsync(string person, ChatTargetKind kind = ChatTargetKind.Auto, CancellationToken ct = default)
        {
            Calls.Add($"open:{person}");
            OpenKinds.Add(kind);
            return Task.FromResult(new ActionResult
            {
                Success = OpenOk,
                Detail = "opened",
                Error = OpenOk ? null : "名字没能输进微信的搜索框（输入通道被吞）",
            });
        }

        public Task<ActionResult> SendMessageAsync(string text, CancellationToken ct = default)
        {
            Calls.Add($"send:{text}");
            return Task.FromResult(new ActionResult { Success = true, Detail = "sent" });
        }

        public Task<ActionResult> WakeWindowAsync(CancellationToken ct = default)
            => Task.FromResult(new ActionResult { Success = true, Detail = "waked" });
    }

    private static ActionSkill Skill(IWeChatActionBridge bridge, ISessionDirectory dir) => new(
        bridge,
        new AlwaysApproveActionConfirmation(),
        targets: new WeChatTargetResolver(dir));

    [Fact]
    public async Task ActionSkill_ResolvesTargetFirst_AndUsesDirectoryKindAndName()
    {
        var dir = new FakeDirectory(Hit("c1", "紫薯马国敬", ts: 5));
        var bridge = new RecordingBridge();
        var events = new List<AgentProgress>();

        await Skill(bridge, dir).ExecuteAsync(
            new SkillRequest { Query = "给紫薯发消息：晚上见", OnTool = events.Add },
            CancellationToken.None);

        // 台账上必须看得到"先核对、再动手"这一步（用户此前完全看不到它）。
        // 顺序：**核对会话目录 → 问用户 → 看微信在不在 → 打开会话 → 发送**
        // （核对在白名单/确认之前：白名单要拿核对后的真名来判，弹窗里显示的也才是真名）
        Assert.Equal(
            ["find_sessions", "find_sessions", "request_user_confirmation", "request_user_confirmation",
             "wechat_status", "wechat_status", "open_chat", "open_chat", "send_message", "send_message"],
            events.Select(e => e.ToolName));
        Assert.True(events[1].ToolSuccess);
        Assert.Contains("紫薯马国敬", events[1].ToolSummary);

        // 名字用库里的，分区按"私聊"而不是 auto
        Assert.Equal(["open:紫薯马国敬", "send:晚上见"], bridge.Calls);
        Assert.Equal(ChatTargetKind.Contact, Assert.Single(bridge.OpenKinds));
    }

    [Fact]
    public async Task ActionSkill_DirectorySaysGroup_OpensGroupSection()
    {
        var dir = new FakeDirectory(Hit("g1", "通项公式研讨群", isGroup: true, ts: 7));
        var bridge = new RecordingBridge();

        await Skill(bridge, dir).ExecuteAsync(
            new SkillRequest { Query = "给通项公式研讨群发消息：收到" }, CancellationToken.None);

        Assert.Equal(ChatTargetKind.Group, Assert.Single(bridge.OpenKinds));
    }

    [Fact]
    public async Task ActionSkill_OpenFailure_SaysTargetExists_NotMissingPerson()
    {
        // 用户看到的正是这句话说错："怀疑没有这个人"——核对结论必须把归因纠正过来
        var dir = new FakeDirectory(Hit("c1", "求一下通项公式", ts: 5));
        var bridge = new RecordingBridge { OpenOk = false };

        var r = await Skill(bridge, dir).ExecuteAsync(
            new SkillRequest { Query = "给求一下通项公式发消息：你好" }, CancellationToken.None);

        Assert.False(r.Success);
        Assert.Contains("会话目录里**有**", r.Error);
        Assert.Contains("不是「没这个人」", r.Error);
        Assert.Contains("wechat_wake_window", r.Error);       // 给出可执行的下一步
    }

    [Fact]
    public async Task ActionSkill_NotFoundInDirectory_SaysSoWithoutClaimingMissing()
    {
        var bridge = new RecordingBridge { OpenOk = false };

        var r = await Skill(bridge, new FakeDirectory()).ExecuteAsync(
            new SkillRequest { Query = "给查无此人发消息：你好" }, CancellationToken.None);

        Assert.False(r.Success);
        Assert.Contains("也没匹配到", r.Error);
        Assert.Equal(ChatTargetKind.Auto, Assert.Single(bridge.OpenKinds));   // 没核对到 → 不猜分区，用 auto 继续
    }

    // ---------- 工具路径：同一条规则 ----------

    private static ToolRegistry Registry(IWeChatActionBridge bridge, ISessionDirectory dir)
    {
        var registry = new ToolRegistry();
        new MemoryAssistant.Infrastructure.Agent.WeChatActionToolProvider(
            bridge,
            new AlwaysApproveActionConfirmation(),
            targets: new WeChatTargetResolver(dir)).RegisterAll(registry);
        return registry;
    }

    [Fact]
    public async Task Tool_Send_UsesDirectoryNameAndKind_AndReportsToModel()
    {
        var dir = new FakeDirectory(Hit("c1", "紫薯马国敬", ts: 5));
        var bridge = new RecordingBridge();
        var registry = Registry(bridge, dir);

        Assert.True(registry.TryGet("wechat_send_message", out var tool));
        var result = await tool.ExecuteAsync("""{"text":"晚上见","person":"紫薯"}""", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("open:紫薯马国敬", bridge.Calls[0]);
        Assert.Equal(ChatTargetKind.Contact, Assert.Single(bridge.OpenKinds));
        Assert.Contains("紫薯马国敬", result.Output);
        Assert.Contains("会话目录里有", result.Output);      // 模型看得到事实，就不会自己编"没找到"
    }

    [Fact]
    public async Task Tool_OpenFailure_TellsModelItIsWindowTrouble()
    {
        var dir = new FakeDirectory(Hit("c1", "求一下通项公式", ts: 5));
        var bridge = new RecordingBridge { OpenOk = false };
        var registry = Registry(bridge, dir);

        Assert.True(registry.TryGet("wechat_send_message", out var tool));
        var result = await tool.ExecuteAsync("""{"text":"你好","person":"求一下通项公式"}""", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("会话目录里**有**", result.Error);
        Assert.DoesNotContain(bridge.Calls, c => c.StartsWith("send:", StringComparison.Ordinal));  // 打开失败绝不发送
    }
}
