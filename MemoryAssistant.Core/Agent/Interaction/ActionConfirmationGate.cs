using MemoryAssistant.Core.Agent.Action;
using MemoryAssistant.Core.Agent.Interaction;

namespace MemoryAssistant.Core.Agent.Interaction;

/// <summary>
/// 写操作确认闸门 → 通用交互口 的适配器（规范 §4：只保留一套确认机制）。
///
/// 以前 IActionConfirmation 是一套独立的弹窗，Agent 自己不知道"可以问用户"；
/// 现在它只是"通用交互口 + 固定几个选项"的一层薄适配：
///   Agent（工具 request_user_confirmation）┐
///                                        ├→ IUserInteraction → UI → User → Tool Result → Agent
///   ActionSkill / 写操作工具（IActionConfirmation）┘
///
/// 第三阶段补充（授权机制）：弹窗多一个「以后都允许」，用户点了就落一条
/// <see cref="ActionGrant"/>——之后**同一对象、同类动作**直接执行，不再打断他；
/// 别人的消息、别的动作照样要问。授权是用户显式给的（只此一处能产生），不是代码里默认放行。
/// </summary>
public sealed class ActionConfirmationGate(IUserInteraction ui, IActionGrantStore? grants = null, Action<string>? audit = null)
    : IActionConfirmation
{
    /// <summary>写操作闸门的确认项（UI 用它识别"这是高风险写操作"）。</summary>
    public const string ConfirmOption = "确认执行";

    /// <summary>授权项：点了它，同一对象的同类写操作以后不再询问。</summary>
    public const string AlwaysOption = "以后都允许";

    /// <summary>取消项。</summary>
    public const string CancelOption = "取消";

    /// <summary>写操作弹窗的标题（App 层的测试旁路据此识别"这是写操作闸门"）。</summary>
    public const string WriteActionTitle = "这一步会真的操作你的微信";

    private readonly IUserInteraction _ui = ui;
    private readonly IActionGrantStore? _grants = grants;
    private readonly Action<string>? _audit = audit;

    public async Task<bool> ConfirmAsync(ActionProposal proposal, CancellationToken ct = default)
    {
        // 入口留痕：出了"工具调用了但弹窗没出来"这类问题，日志能看出是卡在闸门之前还是之后
        _audit?.Invoke($"[Action] 闸门收到写操作确认请求：{proposal.Action} → 「{proposal.Target}」");

        // 已经授权过（用户此前点过「以后都允许」）→ 不再打扰他，但仍留一条日志，
        // 这样"为什么这次没弹窗"在日志里查得到，而不是让用户以为闸门被悄悄关掉了。
        if (_grants?.IsGranted(proposal.Action, proposal.Target) == true)
        {
            _audit?.Invoke($"[Action] 命中记住的授权（对象「{proposal.Target}」），直接执行：{proposal.Description}");
            return true;
        }

        // 没有具体对象（如"回复：好的"= 往当前会话发）就没有授权范围可言：
        // 记住它等于"以后不问对象就放行"，那是把闸门拆了，所以那种情况不给这个选项。
        bool canRemember = !string.IsNullOrWhiteSpace(proposal.Target);
        var options = new List<string> { ConfirmOption };
        if (canRemember) options.Add(AlwaysOption);
        options.Add(CancelOption);

        var choice = await _ui.RequestChoiceAsync(new UserChoiceRequest
        {
            Title = WriteActionTitle,
            Question = "确认执行这个操作吗？",
            Options = options,
            // 默认落在"取消"：回车/误触不会把消息发出去
            DefaultOption = CancelOption,
            Context = proposal.Description
                      + (canRemember
                          ? $"\n（选「{AlwaysOption}」= 以后给「{proposal.Target}」的同类操作都不再询问；"
                            + "授权记录在 data/action-grants.json，删掉该条即可撤销）"
                          : ""),
        }, ct);

        if (choice.Cancelled) return false;

        if (canRemember && choice.Selection == AlwaysOption)
        {
            _grants?.Grant(proposal.Action, proposal.Target);
            _audit?.Invoke($"[Action] 已记住授权：以后对「{proposal.Target}」的同类操作不再询问");
            return true;
        }

        return choice.Selection == ConfirmOption;
    }
}
