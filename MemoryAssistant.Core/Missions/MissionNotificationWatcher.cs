namespace MemoryAssistant.Core.Missions;

/// <summary>
/// 任务通知观察者：隔一会儿看一眼"刚跑完的任务记录"，交给专用通知智能体裁决，
/// 值得打断就在系统通知里弹一条（App 层用托盘气泡实现，点击可把窗口叫回来）。
///
/// 为什么是"轮询 + 批量"而不是"每跑完一条就通知"：
///   · 任务可能几秒内连着跑几轮（实测那个每 40 秒的任务），一条一弹就是刷屏；
///   · 批量交给智能体，它能一起看、择优播报，还能把结论压成一句人话；
///   · 中间隔一段时间（去抖 + 最小间隔）也顺带把通知成本压下来（一次模型调用对应多条记录）。
/// </summary>
public sealed class MissionNotificationWatcher : IDisposable
{
    private readonly MissionRunCursor _cursor;
    private readonly MissionNotificationAgent _agent;
    private readonly INotificationSink _sink;
    private readonly Action<string>? _audit;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _minGap;
    private readonly Timer _timer;

    private DateTimeOffset _lastNotifiedAt = DateTimeOffset.MinValue;

    public MissionNotificationWatcher(
        IMissionRunStore store,
        MissionNotificationAgent agent,
        INotificationSink sink,
        TimeSpan? interval = null,
        TimeSpan? minGap = null,
        Action<string>? audit = null)
    {
        _cursor = new MissionRunCursor(store);
        _agent = agent;
        _sink = sink;
        _audit = audit;
        _minGap = minGap ?? TimeSpan.FromSeconds(60);
        // 启动时把游标推到最新：只播报"应用启动之后"发生的事，别把历史记录翻出来当新闻播一遍
        _cursor.PrimeToNow();

        var every = interval ?? TimeSpan.FromSeconds(20);
        _timer = new Timer(_ => _ = CheckOnceAsync(), null, every, every);
    }

    /// <summary>
    /// 跑一次检查：取新记录 → 交给通知智能体裁决 → 值得就通知。
    /// 定时器按节奏调它；测试直接调它就能确定性验证（不用 sleep 等定时器）。
    /// </summary>
    public async Task CheckOnceAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) return;   // 上一轮还没完 → 跳过
        try
        {
            var fresh = _cursor.NewRecords();
            if (fresh.Count == 0) return;

            var digest = await _agent.DecideAsync(fresh, ct);
            if (!digest.WorthNotifying)
            {
                _audit?.Invoke($"[通知] 本轮 {fresh.Count} 条任务记录，裁决：不值得打断");
                return;
            }
            if (DateTimeOffset.Now - _lastNotifiedAt < _minGap)
            {
                _audit?.Invoke($"[通知] 裁决值得（{digest.Title}），但距上次通知不足 {_minGap.TotalSeconds:0} 秒，本轮略过");
                return;
            }

            _lastNotifiedAt = DateTimeOffset.Now;
            _sink.Notify(digest.Title, digest.Body);
            _audit?.Invoke($"[通知] 已通知：{digest.Title}｜{digest.Body}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 通知失败绝不能影响任务执行本身
            _audit?.Invoke($"[通知] 处理失败（不影响任务执行）：{ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _gate.Dispose();
    }
}
