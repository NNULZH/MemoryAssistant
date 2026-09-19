namespace MemoryAssistant.Core.Agent.Interaction;

/// <summary>
/// Agent 向用户提出的一个"要你做决定"的请求。
///
/// 刻意做成**通用**的（不是 confirm_send_message）：确认写操作、二选一、挑候选、
/// 多个搜索结果里选一个继续深入……都是同一个形状。
/// </summary>
public sealed record UserChoiceRequest
{
    /// <summary>弹窗标题（默认"Agent 请求你的决定"）。</summary>
    public string Title { get; init; } = "Agent 请求你的决定";

    /// <summary>问题本身（一句话说清楚要用户决定什么）。</summary>
    public string Question { get; init; } = "";

    /// <summary>可选项（至少两个）。返回给 Agent 的就是这里面的某一项原文。</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>默认选项（回车就走它；UI 需明确标出，避免误触高风险项）。</summary>
    public string DefaultOption { get; init; } = "";

    /// <summary>背景信息（要发送的内容、候选对象的来源、将影响的文件…）。可为空。</summary>
    public string Context { get; init; } = "";
}

/// <summary>用户的选择结果。取消（关窗/Esc）时 <see cref="Cancelled"/> 为 true、Selection 为空。</summary>
public sealed record UserChoice(string Selection, bool Cancelled)
{
    public static UserChoice Cancel() => new("", true);
}

/// <summary>
/// Agent ↔ 用户 的**唯一**交互口。
///
/// 为什么要有它：以前"需要用户拍板"这件事只藏在 ActionSkill 内部（写操作弹个确认框），
/// Agent 自己根本不知道"我可以问用户"。现在把它做成一等能力——
/// Agent 通过工具 request_user_confirmation 调用它，写操作闸门也走同一个实现，
/// 界面上只有**一套**确认/选择的交互（规范 §4）。
/// </summary>
public interface IUserInteraction
{
    /// <summary>把选择请求交给宿主 UI，等用户的决定。</summary>
    Task<UserChoice> RequestChoiceAsync(UserChoiceRequest request, CancellationToken ct = default);
}

/// <summary>
/// 安全默认实现：没有人可以问（命令行/无 UI 自检）时一律返回"取消"。
/// 宁可什么都不做，也不替用户做主。
/// </summary>
public sealed class DenyAllUserInteraction : IUserInteraction
{
    public Task<UserChoice> RequestChoiceAsync(UserChoiceRequest request, CancellationToken ct = default)
        => Task.FromResult(UserChoice.Cancel());
}
