using MemoryAssistant.Core.Configuration;

namespace MemoryAssistant.Tests;

/// <summary>
/// V4.1 刷新节流：把"数据刷新别那么快"的语义钉死——尤其 <c>0 = 只在手动刷新时加载</c>。
/// 时钟可注入，测试不 sleep。
/// </summary>
public sealed class AutoRefreshPolicyTests
{
    [Fact]
    public void LoadsOnFirstVisit_ThenThrottlesUntilIntervalElapses()
    {
        var now = DateTimeOffset.Parse("2026-09-13T10:00:00+08:00");
        var p = new AutoRefreshPolicy(60, () => now);

        Assert.True(p.ShouldAutoLoad());     // 从没加载过 → 必须加载（否则页面一开始就是空的）
        p.MarkLoaded();

        now = now.AddSeconds(59);
        Assert.False(p.ShouldAutoLoad());    // 差 1 秒 → 切回来不重拉
        now = now.AddSeconds(1);
        Assert.True(p.ShouldAutoLoad());     // 满 60 秒 → 允许重拉
    }

    [Fact]
    public void Zero_MeansManualOnly()
    {
        var now = DateTimeOffset.Parse("2026-09-13T10:00:00+08:00");
        var p = new AutoRefreshPolicy(0, () => now);

        Assert.True(p.ShouldAutoLoad());
        p.MarkLoaded();

        now = now.AddHours(5);
        Assert.False(p.ShouldAutoLoad());    // 关掉自动刷新后，无论过多久都不自动拉
        Assert.Equal("自动刷新已关闭（点「刷新」才加载）", p.Hint);
    }

    [Fact]
    public void NegativeInterval_IsTreatedAsOff()
    {
        var p = new AutoRefreshPolicy(-5);

        Assert.Equal(0, p.AutoRefreshSeconds);
        Assert.Contains("自动刷新已关闭", p.Hint);
    }
}
