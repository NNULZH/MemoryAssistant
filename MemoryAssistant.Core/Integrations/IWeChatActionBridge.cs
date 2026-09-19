namespace MemoryAssistant.Core.Integrations;

/// <summary>一次微信写操作（Action）的执行结果。</summary>
public sealed record ActionResult
{
    public bool Success { get; init; }
    /// <summary>成功时的过程说明（如"已通过搜索打开会话"）。</summary>
    public string Detail { get; init; } = "";
    public string? Error { get; init; }
}

/// <summary>
/// 搜索结果该点哪一组：微信的会话搜索下拉是按「最常使用 / 联系人 / 群聊」**分区**展示的，
/// 同一个关键词会在多个分区出现——搜一个人名时，「群聊」区里全是"消息里提到过她的群"，
/// 而真正的那个人在「联系人」区。不指定分区就只能靠猜，实测会点进一个无关的群。
/// </summary>
public enum ChatTargetKind
{
    /// <summary>不确定：优先「联系人」分区，没有该分区再退到「群聊」。</summary>
    Auto = 0,
    /// <summary>联系人（私聊）。</summary>
    Contact = 1,
    /// <summary>群聊。</summary>
    Group = 2,
}

/// <summary>
/// 微信写操作桥（V3.4，Action 类）：打开会话 / 发送消息。
/// 与只读桥 <see cref="IWeChatWindowBridge"/> 分离，便于"默认只读"的白名单控制。
/// 本接口自身不做确认——确认由上层 <c>IActionConfirmation</c> 强制（见 ActionSkill）。
/// "回复当前会话"= 不先打开、直接 <see cref="SendMessageAsync"/>，故不单独设方法。
/// </summary>
public interface IWeChatActionBridge
{
    /// <summary>当前环境是否具备写能力（能找到微信窗口）。</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 打开与指定对象的会话（搜索 → 按 <paramref name="kind"/> 定位分区 → 打开）。
    /// </summary>
    Task<ActionResult> OpenChatAsync(
        string person, ChatTargetKind kind = ChatTargetKind.Auto, CancellationToken ct = default);

    /// <summary>向"当前已打开的会话"发送一条消息。</summary>
    Task<ActionResult> SendMessageAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// 唤醒 / 回正微信窗口（第三阶段补充）：最小化或托盘先还原、失焦重新抢回键盘焦点、
    /// 卡住的浮层在窗口空白处点一下收掉，并等到窗口位置稳定。
    ///
    /// 为什么单独做成一个能力：实测"窗口卡住"只要在空白处点一下就恢复，但 Agent 自己不会点。
    /// 有了它，写/读操作返回"未就绪/失焦/输入通道卡住"时，Agent 可以先唤醒再重试，而不是直接放弃。
    /// </summary>
    Task<ActionResult> WakeWindowAsync(CancellationToken ct = default);
}
