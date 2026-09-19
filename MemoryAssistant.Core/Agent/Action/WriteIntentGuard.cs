namespace MemoryAssistant.Core.Agent.Action;

/// <summary>
/// 写操作意图守卫（第三阶段补充 §1/§3/§4）：
/// **用户明确要求"发消息/回复/打开某个会话"时，本轮必须真的发生过一次对应的工具调用**，
/// 否则不许当成功汇报——模型"说自己发了"和"真的发了"是两件完全不同的事。
///
/// 实测踩过的坑：用户说"给文件传输助手发一条：测试消息"，规划器选了 action 技能，
/// 但写操作在"环境检查"那一步就返回了（微信不在前台/窗口没找到），
/// 于是模型回了一句"我这就发…你点一下就行"——用户以为已经在执行，实际上什么都没发生。
///
/// 这里给 Runtime 一个可判定的判据：意图 → 必须出现哪个工具名；以及"这次到底有没有真的调用成功"。
/// </summary>
public static class WriteIntentGuard
{
    /// <summary>
    /// 会被算作"真的执行了写操作"的工具名。
    ///
    /// 两套名字都要算：`wechat_*` 是工具注册表里的名字（LLM 自主选工具路径），
    /// `open_chat` / `send_message` 是 ActionSkill 内部推进度台账时用的名字（规则路径）。
    /// **实测教训**：只认 wechat_* 时，规则路径明明真的把消息发出去了，也会被判成"没执行"，
    /// 于是纠正轮又用工具路径把同一条消息**再发一遍**（对方收到两条）。
    /// </summary>
    public static readonly string[] WriteToolNames =
        ["wechat_send_message", "wechat_open_chat", "send_message", "open_chat"];

    /// <summary>发消息类工具（用户要求"发/回复"时应该被调用的那个）。</summary>
    public const string SendTool = "wechat_send_message";

    /// <summary>打开会话类工具（只要求"打开某个聊天"时用）。</summary>
    public const string OpenTool = "wechat_open_chat";

    /// <summary>
    /// 从用户原话判断"这是不是一条写操作指令"，是的话返回**必须被调用**的工具名；否则 null。
    /// 直接用既有的规则解析器（与 ActionSkill 同一套判据，不另立第二套规则）。
    /// 传 lastPerson 才能认住省略收件人的追问（"再发送一句X"）——不传就等于没有这条能力。
    /// </summary>
    public static string? RequiredTool(string? query, string? lastPerson = null)
    {
        var parsed = ActionIntentParser.TryParse(query ?? "", lastPerson);
        if (parsed is null) return null;
        return parsed.Payload.Length > 0 ? SendTool : OpenTool;
    }

    /// <summary>本轮是否真的成功调用过写操作工具。</summary>
    public static bool HasSuccessfulWriteCall(IEnumerable<ToolCallTrace>? calls)
    {
        if (calls is null) return false;
        foreach (var c in calls)
            if (c.Success && WriteToolNames.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>本轮是否**尝试过**写操作工具（不管成败），用于区分"没试"和"试了但失败"。</summary>
    public static bool HasAnyWriteCall(IEnumerable<ToolCallTrace>? calls)
    {
        if (calls is null) return false;
        foreach (var c in calls)
            if (WriteToolNames.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// 用户自己在确认弹窗里点了取消。
    /// 这**不算**"没执行"：用户就是不想发——既不该再问一遍，也不该在回答里说"模型没调工具"。
    /// </summary>
    public static bool WasCancelledByUser(IEnumerable<ToolCallTrace>? calls)
        => calls?.Any(c => c.Name == "request_user_confirmation"
                           && c.Output.Contains("取消", StringComparison.Ordinal)) == true;

    /// <summary>
    /// "发送结果无法确认"的标记短语（发送桥与 Runtime 校验共用同一份字面量）。
    ///
    /// 什么情况会出现它：回车后只拿到**半条**证据（聊天历史里看到了但输入框没清空，或反过来）——
    /// 消息很可能已经发出去了，只是屏幕判据没凑齐。这时既不能报成功（那是假成功），
    /// 也**绝不能自动重发**（对方会收到两条），所以单独标成"无法确认"，由用户自己看一眼定夺。
    /// </summary>
    public const string IndeterminateMarker = "发送结果无法确认";

    /// <summary>本轮是否出现过"无法确认"的写操作（这种失败禁止自动重试，避免重复发送）。</summary>
    public static bool WasIndeterminate(IEnumerable<ToolCallTrace>? calls)
        => calls?.Any(c => !c.Success && c.Error?.Contains(IndeterminateMarker, StringComparison.Ordinal) == true)
           == true;

    /// <summary>
    /// Runtime 纠正轮要发给模型的话：把"必须调工具"讲清楚（tool_choice 之外的第二道保险，
    /// 也是不支持 tool_choice 的客户端唯一的强制手段）。
    /// </summary>
    public static string CorrectionInstruction(string toolName, string query)
        => $"用户明确要求执行一个写操作：「{query}」。\n"
         + $"你刚才只回复了文字，**并没有真的调用 {toolName}**——那等于假装执行了，用户会被误导。\n"
         + $"现在请立刻调用 {toolName}（按它声明的协议把参数给全：person / text / kind 等）。\n"
         + "如果工具报错或用户取消，就如实说明；**不要再只发一段文字说'已经/将要发送'**。";
}
