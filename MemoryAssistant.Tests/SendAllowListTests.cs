using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Action;
using MemoryAssistant.Core.Agent.Interaction;
using MemoryAssistant.Core.Agent.Skills;
using MemoryAssistant.Core.Integrations;

namespace MemoryAssistant.Tests;

/// <summary>
/// 第三阶段补充（强规则·发送对象白名单）：
/// 名单非空时**只有名单内的对象能被发送**，名单外**连确认弹窗都不弹，直接拒绝**。
///
/// 与确认弹窗的分工是这组测试的核心口径：
///   确认 = 用户当场拍板（可能被惯性点过去）；白名单 = 事前定好的死规矩（消息根本发不出去）。
/// 判定必须用**会话目录核对后的真名**——否则"给周斌发消息"点到"周斌的工作群"就绕过去了。
/// </summary>
public sealed class SendAllowListTests
{
    // ---------- 策略本身 ----------

    [Fact]
    public void EmptyList_IsDisabled_AllowsEverything()
    {
        var list = new SendAllowList();

        Assert.False(list.Enabled);
        Assert.True(list.Allows("任何人"));
        Assert.True(list.Allows(""));       // 不启用时"发给当前会话"也照旧放行
    }

    [Fact]
    public void EnabledList_AllowsOnlyListedNames()
    {
        var list = new SendAllowList(["周斌", " 文件传输助手 "]);   // 顺手验证首尾空格会被裁掉

        Assert.True(list.Enabled);
        Assert.True(list.Allows("周斌"));
        Assert.True(list.Allows("文件传输助手"));
        Assert.True(list.Allows("周斌", "别名"));                    // 任一候选命中即放行
        Assert.False(list.Allows("张晓明"));
        Assert.False(list.Allows(""));
    }

    [Fact]
    public void EnabledList_IsCaseAndSpaceInsensitive()
    {
        var list = new SendAllowList(["Alice"]);

        Assert.True(list.Allows(" alice "));
    }

    [Fact]
    public void RejectionFor_NamesTheTargetAndTellsHowToFix()
    {
        var list = new SendAllowList(["周斌"]);

        var text = list.RejectionFor("张晓明");

        Assert.Contains("「张晓明」", text);
        Assert.Contains("不在发送白名单内", text);
        Assert.Contains("未发送任何消息", text);
        Assert.Contains("周斌", text);                    // 名单内容要让用户看得见
        Assert.Contains("agent.sendAllowList", text);     // 以及怎么改
    }

    // ---------- action Skill ----------

    private sealed class RecordingBridge : IWeChatActionBridge
    {
        public List<string> Calls { get; } = [];
        public List<ChatTargetKind> OpenKinds { get; } = [];
        public bool IsAvailable => true;

        public Task<ActionResult> OpenChatAsync(string person, ChatTargetKind kind = ChatTargetKind.Auto, CancellationToken ct = default)
        {
            Calls.Add($"open:{person}");
            OpenKinds.Add(kind);
            return Task.FromResult(new ActionResult { Success = true, Detail = "opened" });
        }

        public Task<ActionResult> SendMessageAsync(string text, CancellationToken ct = default)
        {
            Calls.Add($"send:{text}");
            return Task.FromResult(new ActionResult { Success = true, Detail = "sent" });
        }

        public Task<ActionResult> WakeWindowAsync(CancellationToken ct = default)
            => Task.FromResult(new ActionResult { Success = true, Detail = "waked" });
    }

    private sealed class CountingConfirmation : IActionConfirmation
    {
        public int Calls { get; private set; }
        public Task<bool> ConfirmAsync(ActionProposal proposal, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeDirectory(params SessionHit[] hits) : ISessionDirectory
    {
        public Task<IReadOnlyList<SessionHit>> FindAsync(string keyword, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SessionHit>>(hits);
    }

    private static ActionSkill Skill(
        IWeChatActionBridge bridge, IActionConfirmation confirmation, SendAllowList list, ISessionDirectory? dir = null)
        => new(bridge, confirmation, targets: dir is null ? null : new WeChatTargetResolver(dir), sendAllowList: list);

    [Fact]
    public async Task ActionSkill_TargetOutsideList_IsRefusedWithoutAskingUser()
    {
        var bridge = new RecordingBridge();
        var confirmation = new CountingConfirmation();

        var r = await Skill(bridge, confirmation, new SendAllowList(["周斌"]))
            .ExecuteAsync(new SkillRequest { Query = "给张晓明发消息：你好" }, CancellationToken.None);

        Assert.False(r.Success);
        Assert.Contains("不在发送白名单内", r.Error);
        Assert.Equal(0, confirmation.Calls);       // 关键：连确认弹窗都没弹
        Assert.Empty(bridge.Calls);                // 也一个动作都没做
    }

    [Fact]
    public async Task ActionSkill_TargetInsideList_StillGoesThroughNormalConfirm()
    {
        var bridge = new RecordingBridge();
        var confirmation = new CountingConfirmation();

        var r = await Skill(bridge, confirmation, new SendAllowList(["周斌"]))
            .ExecuteAsync(new SkillRequest { Query = "给周斌发消息：晚上见" }, CancellationToken.None);

        Assert.True(r.Success);
        Assert.Equal(1, confirmation.Calls);       // 白名单放行后，仍然按老规矩问一次
        Assert.Equal(["open:周斌", "send:晚上见"], bridge.Calls);
    }

    [Fact]
    public async Task ActionSkill_UsesResolvedNameForWhitelist_SoNicknameStillMatches()
    {
        // 用户说"给紫薯发消息"，会话目录里真名是"紫薯马国敬"：名单里有真名就该放行。
        // 反过来，如果核对出的是"紫薯马国敬的群"，而名单里只有"紫薯马国敬"，就必须拦住——
        // 这正是"白名单要拿真名判"的意义。
        var bridge = new RecordingBridge();
        var dir = new FakeDirectory(new SessionHit("c1", "紫薯马国敬", false, 5));

        var r = await Skill(bridge, new CountingConfirmation(),
                new SendAllowList(["紫薯马国敬"]), dir)
            .ExecuteAsync(new SkillRequest { Query = "给紫薯发消息：晚上见" }, CancellationToken.None);

        Assert.True(r.Success);
        Assert.Equal(["open:紫薯马国敬", "send:晚上见"], bridge.Calls);
    }

    [Fact]
    public async Task ActionSkill_ResolvedToGroup_IsRefusedEvenThoughQueryMatched()
    {
        var bridge = new RecordingBridge();
        var dir = new FakeDirectory(new SessionHit("g1", "紫薯马国敬的群", true, 99));

        var r = await Skill(bridge, new CountingConfirmation(),
                new SendAllowList(["紫薯马国敬"]), dir)
            .ExecuteAsync(new SkillRequest { Query = "给紫薯马国敬发消息：你好" }, CancellationToken.None);

        Assert.False(r.Success);
        Assert.Contains("不在发送白名单内", r.Error);
        Assert.Empty(bridge.Calls);
    }

    [Fact]
    public async Task ActionSkill_ReplyToCurrentChat_IsRefusedWhenListEnabled()
    {
        // "回复：好的"没有收件人 → 白名单启用时无法确认收件人，必须拒绝（不能赌当前会话是谁）
        var bridge = new RecordingBridge();
        var confirmation = new CountingConfirmation();

        var r = await Skill(bridge, confirmation, new SendAllowList(["周斌"]))
            .ExecuteAsync(new SkillRequest { Query = "回复：好的" }, CancellationToken.None);

        Assert.False(r.Success);
        Assert.Contains("当前会话", r.Error);
        Assert.Equal(0, confirmation.Calls);
        Assert.Empty(bridge.Calls);
    }

    [Fact]
    public async Task ActionSkill_OpenOnly_IsNotBlockedByWhitelist()
    {
        // 白名单管的是"发送对象"：只打开会话（不发消息）不该被拦——它没有把话传出去
        var bridge = new RecordingBridge();

        var r = await Skill(bridge, new CountingConfirmation(), new SendAllowList(["周斌"]))
            .ExecuteAsync(new SkillRequest { Query = "打开张晓明的聊天" }, CancellationToken.None);

        Assert.True(r.Success);
        Assert.Equal(["open:张晓明"], bridge.Calls);
    }

    // ---------- 工具路径：同一条规矩 ----------

    [Fact]
    public async Task Tool_SendOutsideList_IsRefusedWithoutAskingUser()
    {
        var bridge = new RecordingBridge();
        var confirmation = new CountingConfirmation();
        var registry = new ToolRegistry();
        new MemoryAssistant.Infrastructure.Agent.WeChatActionToolProvider(
            bridge, confirmation, sendAllowList: new SendAllowList(["周斌"])).RegisterAll(registry);

        Assert.True(registry.TryGet("wechat_send_message", out var tool));
        var result = await tool.ExecuteAsync("""{"text":"你好","person":"张晓明"}""", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("不在发送白名单内", result.Error);
        Assert.Equal(0, confirmation.Calls);
        Assert.Empty(bridge.Calls);
    }

    [Fact]
    public async Task Tool_SendToCurrentChat_IsRefusedWhenListEnabled()
    {
        var bridge = new RecordingBridge();
        var registry = new ToolRegistry();
        new MemoryAssistant.Infrastructure.Agent.WeChatActionToolProvider(
            bridge, new AlwaysApproveActionConfirmation(),
            sendAllowList: new SendAllowList(["周斌"])).RegisterAll(registry);

        Assert.True(registry.TryGet("wechat_send_message", out var tool));
        var result = await tool.ExecuteAsync("""{"text":"你好"}""", CancellationToken.None);   // 没给 person

        Assert.False(result.Success);
        Assert.Contains("当前会话", result.Error);
        Assert.Empty(bridge.Calls);
    }
}
