using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Action;
using MemoryAssistant.Core.Agent.Conversation;
using MemoryAssistant.Core.Agent.Interaction;
using MemoryAssistant.Core.Agent.Planner;
using MemoryAssistant.Core.Agent.Skills;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.Integrations;

namespace MemoryAssistant.Tests;

/// <summary>
/// 第三阶段补充：**省略收件人的发送追问**（「再发送一句X」）。
///
/// 实测事故：用户说了"给张晓明发消息：A"，紧接着说"再发送一句B"——
/// 规则解析器只认「给X发消息 / 跟X说： / 回复：」，这句全部不匹配，
/// 于是 action 技能第一句就判"未能识别"、一个动作都没做（桥、弹窗、白名单全到不了），
/// 而作答器还把它讲成"没等到发送回执"，把人绕得更深。
///
/// 这组测试钉住的口径：
///   ① 有"上一轮的人"→ 补出收件人，真的去发；
///   ② 没有 → **不认**（不能发到"当前碰巧打开的会话"，那是最容易发错人的情况）；
///   ③ 规划器与写操作守卫都要把它当写操作，否则连纠正轮都不会触发；
///   ④ "我最近发送过什么"这种只读提问**不许**被拖进写操作链路。
/// </summary>
public sealed class SendFollowUpTests
{
    // ---------- 解析 ----------

    [Theory]
    [InlineData("再发送一句「来自天堂的ai」", "来自天堂的ai")]
    [InlineData("再发送一句\"来自天堂的ai\"", "来自天堂的ai")]
    [InlineData("再发一条：晚上见", "晚上见")]
    [InlineData("接着发一句 收到", "收到")]
    [InlineData("再发送一条消息：明天见", "明天见")]
    public void FollowUp_WithLastPerson_FillsRecipientAndPayload(string query, string payload)
    {
        var parsed = ActionIntentParser.TryParse(query, "张晓明");

        Assert.NotNull(parsed);
        Assert.Equal("send_message", parsed!.Action);
        Assert.Equal("张晓明", parsed.Target);
        Assert.Equal(payload, parsed.Payload);
    }

    [Fact]
    public void FollowUp_WithoutLastPerson_IsNotAccepted()
    {
        // 没有对象就宁可不认：认了只能发到"当前碰巧打开的那个会话"
        Assert.Null(ActionIntentParser.TryParse("再发送一句「来自天堂的ai」", null));
        Assert.Null(ActionIntentParser.TryParse("再发送一句「来自天堂的ai」", ""));
        Assert.Null(ActionIntentParser.TryParse("再发送一句", "张晓明"));   // 没内容也不认
    }

    [Theory]
    [InlineData("发现这个问题了")]          // 少了"条/句/段"量词 → 绝不能当成"发送：现这个问题"
    [InlineData("我最近发送过什么消息")]     // 只读提问
    [InlineData("发送失败率高吗")]
    public void NotA_FollowUpSend(string query)
    {
        Assert.False(ActionIntentParser.IsBareSendFollowUp(query));
        Assert.Null(ActionIntentParser.TryParse(query, "张晓明"));
    }

    [Fact]
    public void RequiredTool_RecognizesFollowUp_SoRuntimeCanForceTheCall()
    {
        // 不认它的话，Runtime 根本不会启动"写操作必须真的调工具"的纠正轮
        Assert.Equal(WriteIntentGuard.SendTool, WriteIntentGuard.RequiredTool("再发送一句「来自天堂的ai」", "张晓明"));
        Assert.Null(WriteIntentGuard.RequiredTool("再发送一句「来自天堂的ai」"));
    }

    // ---------- 会话上下文：写操作的目标要记成"本轮谈到的人" ----------

    [Fact]
    public void Session_RemembersSendTargetAsLastPerson()
    {
        var session = new ConversationSession();

        session.RecordTurn("给张晓明发消息：晚上见", "给张晓明发消息：晚上见", "已经发出去了", []);

        // ExtractPerson 只认"和/跟/与/找/问"，对"给X发消息"是瞎的 → 必须由写操作目标补上
        Assert.Equal("张晓明", session.LastPerson);
    }

    [Fact]
    public void Session_ThenFollowUpResolvesToTheSamePerson()
    {
        var session = new ConversationSession();
        session.RecordTurn("给张晓明发消息：晚上见", "给张晓明发消息：晚上见", "已经发出去了", []);

        var parsed = ActionIntentParser.TryParse("再发送一句「来自天堂的ai」", session.LastPerson);

        Assert.NotNull(parsed);
        Assert.Equal("张晓明", parsed!.Target);
    }

    // ---------- 规划：这类追问要路由到 action 技能 ----------

    [Fact]
    public async Task Planner_RoutesFollowUpSendToActionSkill()
    {
        var planner = new Planner(SkillCatalog.Default(), new AgentOptions(), chat: null);

        var plan = await planner.PlanAsync("再发送一句「来自天堂的ai」", preferLlm: false);

        Assert.Contains(plan.Steps, s => s.Kind == PlanStepKind.Skill && s.Name == "action");
    }

    // ---------- action 技能：真的去发 / 明确说没发 ----------

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

    [Fact]
    public async Task ActionSkill_FollowUpWithLastPerson_ActuallySends()
    {
        var bridge = new RecordingBridge();
        var skill = new ActionSkill(bridge, new AlwaysApproveActionConfirmation());

        var r = await skill.ExecuteAsync(new SkillRequest
        {
            Query = "再发送一句「来自天堂的ai」",
            OriginalQuery = "再发送一句「来自天堂的ai」",
            LastPerson = "张晓明",
        }, CancellationToken.None);

        Assert.True(r.Sufficient);
        Assert.Equal(["open:张晓明", "send:来自天堂的ai"], bridge.Calls);
    }

    [Fact]
    public async Task ActionSkill_FollowUpWithoutLastPerson_SaysNothingWasDone()
    {
        var bridge = new RecordingBridge();
        var audited = new List<string>();
        var skill = new ActionSkill(bridge, new AlwaysApproveActionConfirmation(), audited.Add);

        var r = await skill.ExecuteAsync(new SkillRequest
        {
            Query = "再发送一句「来自天堂的ai」",
            OriginalQuery = "再发送一句「来自天堂的ai」",
        }, CancellationToken.None);

        Assert.True(r.Success);
        Assert.False(r.Sufficient);                 // 不是"做完了"，是"没做"
        Assert.Contains("没有说清", r.Summary);
        Assert.Contains("没有执行任何动作", r.Summary);
        Assert.Empty(bridge.Calls);
        Assert.Contains(audited, a => a.Contains("未执行任何动作"));   // 静默跳步必须留痕
    }
}
