namespace MemoryAssistant.Core.Agent.Runtime;

/// <summary>
/// "正在等用户做决定"的信号（进程内，与界面状态同一套思路）。
///
/// 为什么需要它：任务级超时（<see cref="Configuration.AgentOptions.MaxTaskSeconds"/>）算的是
/// **智能体自己干活花掉的时间**；而"弹窗开着、等用户点确认"这段时间不是它花的——
/// 用户可能正在犹豫，也可能正在给演示讲解。
///
/// 之前这段时间照算，实测后果很严重：一次演示里确认窗开了 8 分钟才被点，
/// 任务早已被判「超过 MaxTaskSeconds(300)」，用户点头之后**什么都不会发生**——
/// 他明确给出的那次批准被静默丢掉，界面上只剩一句"本轮提前结束"。
///
/// 所以等用户期间要暂停计时：任务的预算管"它自己干多久"，不管"人想多久"。
/// </summary>
public static class UserDecisionClock
{
    private static int _openScopes;

    /// <summary>当前是否有决定在等用户（弹窗开着）。</summary>
    public static bool IsWaiting => Volatile.Read(ref _openScopes) > 0;

    /// <summary>
    /// 进入"等用户"区间，离开时 Dispose 即可。允许嵌套（多个弹窗 / 多任务并发），用引用计数。
    /// </summary>
    public static IDisposable Wait()
    {
        Interlocked.Increment(ref _openScopes);
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            // 幂等：重复 Dispose 不能让计数掉到 0 以下，否则以后再也没人暂停计时
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref _openScopes);
        }
    }
}
