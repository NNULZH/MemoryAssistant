namespace MemoryAssistant.Core.Configuration;

/// <summary>
/// 页面"自动重拉"的节流策略（V4.1）。
///
/// 背景（用户反馈"几个多余功能的数据刷新不要那么快"）：时间线/画像/承诺/话题这些页面的 View
/// 都挂在 <c>Loaded</c> 上无条件重拉数据，而页面每次导航都会新建一个 View，
/// 所以**每切一次 Tab 就打一遍接口**（话题页甚至跑一次 LLM 聚类）。切几次就白烧接口与额度。
///
/// 现在的口径：
///   - 距上次加载不足 <see cref="AutoRefreshSeconds"/> 秒 → 切回来不重拉（用的还是已有数据）；
///   - <see cref="AutoRefreshSeconds"/> = 0 → 关掉自动刷新，只在用户点「刷新」时加载；
///   - 从未加载过 → 一定加载（否则页面一开始就是空的）。
/// 每个页面都有手动「刷新」按钮兜底，所以"不自动拉"不会让用户卡住。
///
/// 放在 Core 而不是 App：它是纯逻辑（可单测），也是配置的一部分语义。
/// </summary>
public sealed class AutoRefreshPolicy
{
    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset? _lastLoadedAt;

    public AutoRefreshPolicy(int autoRefreshSeconds, Func<DateTimeOffset>? clock = null)
    {
        AutoRefreshSeconds = Math.Max(0, autoRefreshSeconds);
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>配置的自动刷新间隔（秒）；0 = 关闭自动刷新。</summary>
    public int AutoRefreshSeconds { get; }

    /// <summary>现在该不该自动重拉？</summary>
    public bool ShouldAutoLoad()
    {
        if (_lastLoadedAt is null) return true;                 // 从没加载过 → 必须加载
        if (AutoRefreshSeconds <= 0) return false;              // 已关自动刷新 → 只认手动
        return (_clock() - _lastLoadedAt.Value).TotalSeconds >= AutoRefreshSeconds;
    }

    /// <summary>记录"刚刚成功加载过"，供下次判断是否过期。</summary>
    public void MarkLoaded() => _lastLoadedAt = _clock();

    /// <summary>给 UI 显示的策略说明（让用户知道为什么不刷新、按钮在哪）。</summary>
    public string Hint => AutoRefreshSeconds <= 0
        ? "自动刷新已关闭（点「刷新」才加载）"
        : $"自动刷新间隔 {AutoRefreshSeconds} 秒";
}
