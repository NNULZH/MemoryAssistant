using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Action;
using MemoryAssistant.Core.Agent.Interaction;
using MemoryAssistant.Core.Agent.Skills;
using MemoryAssistant.Core.Integrations;

namespace MemoryAssistant.Tests;

/// <summary>
/// 第三阶段补充（授权机制）：写操作授权的存储 / 闸门 / action Skill 三条路的单测。
/// 核心口径：授权只能由用户在一次确认弹窗里显式点「以后都允许」产生；
/// 授权只覆盖**同一对象 + 同一动作**，且命中授权时代码要说实话（不能假装"用户刚刚确认了"）。
/// </summary>
public sealed class ActionGrantTests
{
    // ---------- 存储 ----------

    [Fact]
    public void Store_Grant_ThenGranted()
    {
        var store = new InMemoryActionGrantStore();
        Assert.False(store.IsGranted("send_message", "周斌"));

        store.Grant("send_message", "周斌");

        Assert.True(store.IsGranted("send_message", "周斌"));
        Assert.Equal("周斌", Assert.Single(store.List()).Target);
    }

    [Fact]
    public void Store_Grant_IsScopedToActionAndTarget()
    {
        var store = new InMemoryActionGrantStore();
        store.Grant("send_message", "周斌");

        Assert.False(store.IsGranted("send_message", "张晓明"));   // 别人：不继承
        Assert.False(store.IsGranted("open_chat", "周斌"));        // 别的动作：不继承
    }

    [Fact]
    public void Store_IgnoresBlankOrDuplicateGrants()
    {
        var store = new InMemoryActionGrantStore();

        store.Grant("send_message", "  ");      // 没有对象 → 不记（否则等于无差别放行）
        store.Grant("", "周斌");
        Assert.Empty(store.List());

        store.Grant("send_message", "周斌");
        store.Grant("send_message", " 周斌 ");  // 同一对象（忽略空格/大小写）不重复记
        Assert.Single(store.List());
    }

    [Fact]
    public void Store_Revoke_RemovesGrant()
    {
        var store = new InMemoryActionGrantStore();
        store.Grant("send_message", "周斌");

        store.Revoke("send_message", "周斌");

        Assert.False(store.IsGranted("send_message", "周斌"));
        Assert.Empty(store.List());
    }

    // ---------- 闸门 ----------

    /// <summary>按预设回答响应的交互口，记录每次被问到的问题。</summary>
    private sealed class ScriptedUserInteraction(string answer, bool cancelled = false) : IUserInteraction
    {
        public List<UserChoiceRequest> Requests { get; } = [];

        public Task<UserChoice> RequestChoiceAsync(UserChoiceRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(cancelled ? UserChoice.Cancel() : new UserChoice(answer, false));
        }
    }

    private static ActionProposal SendProposal(string target = "周斌", string text = "你好") => new()
    {
        Action = "send_message",
        Target = target,
        Payload = text,
        Description = $"打开与「{target}」的微信会话，并发送消息：{text}",
    };

    [Fact]
    public async Task Gate_GrantedTarget_ApprovesWithoutAsking()
    {
        var store = new InMemoryActionGrantStore();
        store.Grant("send_message", "周斌");
        var ui = new ScriptedUserInteraction(ActionConfirmationGate.CancelOption);
        var gate = new ActionConfirmationGate(ui, store);

        var approved = await gate.ConfirmAsync(SendProposal());

        Assert.True(approved);
        Assert.Empty(ui.Requests);      // 关键：这一次没弹窗
    }

    [Fact]
    public async Task Gate_AlwaysOption_RemembersAndSkipsNextTime()
    {
        var store = new InMemoryActionGrantStore();
        var ui = new ScriptedUserInteraction(ActionConfirmationGate.AlwaysOption);
        var gate = new ActionConfirmationGate(ui, store);

        Assert.True(await gate.ConfirmAsync(SendProposal()));
        Assert.True(store.IsGranted("send_message", "周斌"));

        // 第二次同一个对象：直接放行，不再打扰用户
        Assert.True(await gate.ConfirmAsync(SendProposal("周斌", "在吗")));
        Assert.Single(ui.Requests);
    }

    [Fact]
    public async Task Gate_AlwaysOption_DoesNotLeakToOtherTargets()
    {
        var store = new InMemoryActionGrantStore();
        var ui = new ScriptedUserInteraction(ActionConfirmationGate.AlwaysOption);
        var gate = new ActionConfirmationGate(ui, store);

        await gate.ConfirmAsync(SendProposal("周斌"));
        await gate.ConfirmAsync(SendProposal("张晓明"));

        Assert.Equal(2, ui.Requests.Count);     // 换个人仍然要问
    }

    [Fact]
    public async Task Gate_NoTarget_DoesNotOfferRemember()
    {
        // "回复：好的"这种没有对象的写操作：没有授权范围可言，不该给「以后都允许」
        var ui = new ScriptedUserInteraction(ActionConfirmationGate.ConfirmOption);
        var gate = new ActionConfirmationGate(ui, new InMemoryActionGrantStore());

        await gate.ConfirmAsync(new ActionProposal { Action = "send_message", Target = "", Payload = "好的" });

        Assert.DoesNotContain(ActionConfirmationGate.AlwaysOption, Assert.Single(ui.Requests).Options);
        Assert.Equal(ActionConfirmationGate.CancelOption, Assert.Single(ui.Requests).DefaultOption);
    }

    [Fact]
    public async Task Gate_WithTarget_OffersRememberAndKeepsCancelAsDefault()
    {
        var ui = new ScriptedUserInteraction(ActionConfirmationGate.ConfirmOption);
        var gate = new ActionConfirmationGate(ui, new InMemoryActionGrantStore());

        await gate.ConfirmAsync(SendProposal());

        var request = Assert.Single(ui.Requests);
        Assert.Equal(
            [ActionConfirmationGate.ConfirmOption, ActionConfirmationGate.AlwaysOption, ActionConfirmationGate.CancelOption],
            request.Options);
        Assert.Equal(ActionConfirmationGate.CancelOption, request.DefaultOption);   // 回车不会误发
    }

    [Fact]
    public async Task Gate_Cancelled_IsNotApproved()
    {
        var ui = new ScriptedUserInteraction("", cancelled: true);
        var gate = new ActionConfirmationGate(ui, new InMemoryActionGrantStore());

        Assert.False(await gate.ConfirmAsync(SendProposal()));
    }

    // ---------- action Skill：命中授权时要说实话 ----------

    private sealed class RecordingBridge : IWeChatActionBridge
    {
        public List<string> Calls { get; } = [];
        public bool IsAvailable => true;

        public Task<ActionResult> OpenChatAsync(string person, ChatTargetKind kind = ChatTargetKind.Auto, CancellationToken ct = default)
        {
            Calls.Add($"open:{person}");
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

    /// <summary>数一下"被问了几次"的闸门。</summary>
    private sealed class CountingConfirmation : IActionConfirmation
    {
        public int Calls { get; private set; }

        public Task<bool> ConfirmAsync(ActionProposal proposal, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task ActionSkill_WithGrant_SkipsConfirmAndSaysItWasPreAuthorized()
    {
        var store = new InMemoryActionGrantStore();
        store.Grant("send_message", "周斌");
        var confirmation = new CountingConfirmation();
        var bridge = new RecordingBridge();
        var events = new List<AgentProgress>();

        var r = await new ActionSkill(bridge, confirmation, grants: store).ExecuteAsync(
            new SkillRequest { Query = "给周斌发消息：你好", OnTool = events.Add },
            CancellationToken.None);

        Assert.True(r.Sufficient);
        Assert.Equal(0, confirmation.Calls);                    // 没弹窗
        Assert.Equal(["open:周斌", "send:你好"], bridge.Calls);  // 但动作照做
        Assert.Contains("已按记住的授权直接执行", events[1].ToolSummary);
        Assert.Contains(r.ToolCalls, t => t.Name == "request_user_confirmation" && t.Success);
    }

    [Fact]
    public async Task ActionSkill_WithoutGrant_StillAsks()
    {
        var confirmation = new CountingConfirmation();
        var bridge = new RecordingBridge();

        await new ActionSkill(bridge, confirmation, grants: new InMemoryActionGrantStore()).ExecuteAsync(
            new SkillRequest { Query = "给周斌发消息：你好" }, CancellationToken.None);

        Assert.Equal(1, confirmation.Calls);
    }
}
