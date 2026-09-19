namespace MemoryAssistant.Core.Agent.Action;

/// <summary>
/// 一条"以后不用再问"的写操作授权（第三阶段补充：授权机制）。
///
/// 为什么需要它：写操作每次都弹窗确认，同一个对象反复操作时用户会一直被打断；
/// 但"完全不问"又太危险。折中做法是——弹窗上多一个「以后都允许」，
/// 用户点了就在此落一条授权，之后**同一对象、同类动作**直接执行，别人的消息照样要问。
/// </summary>
public sealed record ActionGrant
{
    /// <summary>动作类型：open_chat | send_message。</summary>
    public string Action { get; init; } = "";

    /// <summary>授权对象（人名/群名）。空表示"当前会话"，不落授权（没有对象就没有范围，记住会变成无差别放行）。</summary>
    public string Target { get; init; } = "";

    /// <summary>授权时间（便于用户核对"这条是什么时候给的"）。</summary>
    public DateTimeOffset GrantedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// 写操作授权的存储口。闸门执行前查一次、用户点「以后都允许」时记一条。
/// 实现可落盘（跨重启保留），也可只在内存里（测试/无持久化环境）。
/// </summary>
public interface IActionGrantStore
{
    /// <summary>这个 (动作, 对象) 是否已被授权（对象名忽略大小写与首尾空格）。</summary>
    bool IsGranted(string action, string target);

    /// <summary>记一条授权。动作或对象为空时不记（不产生无差别的放行）。</summary>
    void Grant(string action, string target);

    /// <summary>撤销一条授权。</summary>
    void Revoke(string action, string target);

    /// <summary>当前所有授权（用户核对/界面展示用）。</summary>
    IReadOnlyList<ActionGrant> List();
}

/// <summary>内存实现：默认/测试用，进程退出即失效。</summary>
public sealed class InMemoryActionGrantStore : IActionGrantStore
{
    private readonly List<ActionGrant> _items = [];
    private readonly object _gate = new();

    public bool IsGranted(string action, string target)
    {
        if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(target)) return false;
        lock (_gate)
            return _items.Any(g => Same(g.Action, action) && Same(g.Target, target));
    }

    public void Grant(string action, string target)
    {
        if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(target)) return;
        lock (_gate)
        {
            if (_items.Any(g => Same(g.Action, action) && Same(g.Target, target))) return;
            _items.Add(new ActionGrant
            {
                Action = action.Trim(),
                Target = target.Trim(),
                GrantedAt = DateTimeOffset.Now,
            });
        }
    }

    public void Revoke(string action, string target)
    {
        lock (_gate)
            _items.RemoveAll(g => Same(g.Action, action) && Same(g.Target, target));
    }

    public IReadOnlyList<ActionGrant> List()
    {
        lock (_gate) return _items.ToList();
    }

    private static bool Same(string a, string b)
        => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
