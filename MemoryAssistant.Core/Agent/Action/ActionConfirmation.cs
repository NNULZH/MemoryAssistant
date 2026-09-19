namespace MemoryAssistant.Core.Agent.Action;

/// <summary>
/// 待人工确认的一次写操作（V3.4 安全边界）。
/// <see cref="Description"/> 是给人看的人话，UI 直接展示；其余字段供留痕/审计。
/// </summary>
public sealed record ActionProposal
{
    /// <summary>动作类型：open_chat | send_message。</summary>
    public string Action { get; init; } = "";
    /// <summary>目标会话（人名/群名），无则空。</summary>
    public string Target { get; init; } = "";
    /// <summary>将发送的文本，无则空。</summary>
    public string Payload { get; init; } = "";
    /// <summary>人话描述（确认弹窗正文）。</summary>
    public string Description { get; init; } = "";
}

/// <summary>
/// 写操作人工确认闸门：Agent 想动用户的微信，必须先得到人点头。
/// 实现可弹窗（WPF）、可命令行询问、也可在测试中固定放行/拒绝。
/// </summary>
public interface IActionConfirmation
{
    Task<bool> ConfirmAsync(ActionProposal proposal, CancellationToken ct = default);
}

/// <summary>安全默认实现：拒绝一切写操作。未注入 UI 时使用，保证不会误发消息。</summary>
public sealed class DenyAllActionConfirmation : IActionConfirmation
{
    public Task<bool> ConfirmAsync(ActionProposal proposal, CancellationToken ct = default)
        => Task.FromResult(false);
}

/// <summary>
/// 显式放行实现：仅供带 --yes 的验收/自动化使用，保留确认记录以便留痕核对。
/// </summary>
public sealed class AlwaysApproveActionConfirmation : IActionConfirmation
{
    private readonly List<ActionProposal> _approved = [];

    public IReadOnlyList<ActionProposal> Approved => _approved;

    public Task<bool> ConfirmAsync(ActionProposal proposal, CancellationToken ct = default)
    {
        _approved.Add(proposal);
        return Task.FromResult(true);
    }
}
