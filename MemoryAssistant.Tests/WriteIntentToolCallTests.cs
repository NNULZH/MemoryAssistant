using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Action;
using MemoryAssistant.Core.Configuration;

namespace MemoryAssistant.Tests;

/// <summary>
/// 第三阶段补充 §1/§3/§4 的单测：写操作意图 → 必须真的发生工具调用；
/// 只生成文字不动手时，Runtime 必须当场纠正，纠正无效则**不算正常完成**（不许假装执行）。
/// 零 LLM、零真实微信。
/// </summary>
public sealed class WriteIntentToolCallTests
{
    // ---------- 意图 → 必须调用的工具 ----------

    [Theory]
    [InlineData("给张三发消息：晚上见", "wechat_send_message")]
    [InlineData("给文件传输助手发一条：测试消息", "wechat_send_message")]
    [InlineData("回复：好的", "wechat_send_message")]
    [InlineData("跟张三说一声：晚点到", "wechat_send_message")]
    [InlineData("打开张三的聊天", "wechat_open_chat")]
    [InlineData("打开文件传输助手的会话", "wechat_open_chat")]
    public void RequiredTool_MapsWriteIntentToTool(string query, string expected)
        => Assert.Equal(expected, WriteIntentGuard.RequiredTool(query));

    [Theory]
    [InlineData("我和张三聊过什么")]
    [InlineData("最近有人和我聊过秋招吗？")]
    [InlineData("总结一下我最近都在聊什么")]
    [InlineData("你好")]
    public void RequiredTool_ReturnsNullForReadOnlyQueries(string query)
        => Assert.Null(WriteIntentGuard.RequiredTool(query));

    // ---------- "真的执行了"的判据 ----------

    private static ToolCallTrace Trace(string name, bool ok, string output = "") => new()
    {
        Name = name,
        Success = ok,
        Output = output,
    };

    [Fact]
    public void HasSuccessfulWriteCall_OnlyCountsSuccess()
    {
        Assert.True(WriteIntentGuard.HasSuccessfulWriteCall([Trace("wechat_send_message", true)]));
        Assert.False(WriteIntentGuard.HasSuccessfulWriteCall([Trace("wechat_send_message", false)]));
        Assert.False(WriteIntentGuard.HasSuccessfulWriteCall([Trace("read_messages", true)]));
        Assert.False(WriteIntentGuard.HasSuccessfulWriteCall([]));
    }

    [Fact]
    public void HasSuccessfulWriteCall_CountsActionSkillOwnTraceNames()
    {
        // 规则路径（ActionSkill）台账里记的是 send_message / open_chat；
        // 漏认它们会让纠正轮把同一条消息**再发一遍**（实测踩过）。
        Assert.True(WriteIntentGuard.HasSuccessfulWriteCall([Trace("send_message", true)]));
        Assert.True(WriteIntentGuard.HasSuccessfulWriteCall([Trace("open_chat", true)]));
    }

    [Fact]
    public void WasIndeterminate_DetectsHalfEvidenceSend_SoNoAutoRetry()
    {
        var uncertain = new ToolCallTrace
        {
            Name = "send_message",
            Success = false,
            Error = WriteIntentGuard.IndeterminateMarker + "：聊天历史里看到了这句，输入框没清空",
        };
        Assert.True(WriteIntentGuard.WasIndeterminate([uncertain]));

        // 普通失败（明确没发出去）不算"无法确认"，该重试还得重试
        Assert.False(WriteIntentGuard.WasIndeterminate([Trace("send_message", false, "没打进输入框")]));
        Assert.False(WriteIntentGuard.WasIndeterminate([Trace("send_message", true)]));
    }

    [Fact]
    public void WasCancelledByUser_DetectsCancelChoice()
    {
        var cancelled = WriteIntentGuard.WasCancelledByUser(
            [Trace("request_user_confirmation", true, "用户选择：取消（未做任何事）")]);
        Assert.True(cancelled);

        var approved = WriteIntentGuard.WasCancelledByUser(
            [Trace("request_user_confirmation", true, "用户选择：确认执行")]);
        Assert.False(approved);
    }

    // ---------- Runtime：要求调工具却没调 → 纠正一次；再不动手就不算完成 ----------

    private static AgentOptions Options()
    {
        var o = new AgentOptions();
        return o;
    }

    private static ToolRegistry RegistryWith(ToolDefinition tool)
    {
        var r = new ToolRegistry();
        r.Register(tool);
        return r;
    }

    private static ToolDefinition DummySendTool(List<string> executed) => new()
    {
        Name = "wechat_send_message",
        Description = "发送微信消息（测试用）",
        Category = ToolCategory.Action,
        ReadOnly = false,
        Parameters = [],
        ExecuteAsync = (_, _) =>
        {
            executed.Add("sent");
            return Task.FromResult(new ToolCallResult { Success = true, Output = "已发送" });
        },
    };

    [Fact]
    public async Task Loop_RequiresToolCall_ModelOnlyText_IsCorrectedThenReportedAsNotExecuted()
    {
        // 脚本：第 1 轮只回文字（不动手）→ 第 2 轮仍然只回文字 → 不允许算成功
        var chat = new ScriptedChatClient(
        [
            new ChatResult { Content = "我这就给张三发消息。" },
            new ChatResult { Content = "已经发了。" },
        ]);
        var executed = new List<string>();
        var loop = new AgentLoop(chat, RegistryWith(DummySendTool(executed)), Options());

        var result = await loop.RunAsync("系统提示", "给张三发消息：晚上见",
            requireToolName: "wechat_send_message");

        Assert.Empty(executed);                        // 一次都没真的调工具
        Assert.False(result.CompletedNormally);        // 不许当成正常完成
        Assert.Contains("写操作未执行", result.EarlyStopReason!);
        Assert.Equal(2, chat.Calls.Count);             // 第 1 轮 + 纠正后第 2 轮（仍不动手 → 如实收尾）
    }

    [Fact]
    public async Task Loop_RequiresToolCall_ModelCallsTool_Succeeds()
    {
        var chat = new ScriptedChatClient(
        [
            new ChatResult
            {
                ToolCalls = [new ToolCallRequest { Name = "wechat_send_message", ArgumentsJson = "{}" }],
            },
            new ChatResult { Content = "已发送。" },
        ]);
        var executed = new List<string>();
        var loop = new AgentLoop(chat, RegistryWith(DummySendTool(executed)), Options());

        var result = await loop.RunAsync("系统提示", "给张三发消息：晚上见",
            requireToolName: "wechat_send_message");

        Assert.Equal(["sent"], executed);
        Assert.True(result.CompletedNormally);
        Assert.Single(result.AllToolCalls);
    }

    [Fact]
    public async Task Loop_WithoutRequirement_PlainAnswerIsNormal()
    {
        var chat = new ScriptedChatClient([new ChatResult { Content = "你们聊过秋招。" }]);
        var loop = new AgentLoop(chat, RegistryWith(DummySendTool([])), Options());

        var result = await loop.RunAsync("系统提示", "我和谁聊过秋招？");

        Assert.True(result.CompletedNormally);
        Assert.Equal("你们聊过秋招。", result.Answer);
    }
}
