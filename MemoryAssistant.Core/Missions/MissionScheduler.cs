namespace MemoryAssistant.Core.Missions;

/// <summary>
/// 任务调度器（V3.2 真实实现）：
/// - 托管"运行中"任务，按 Trigger/Interval 周期巡检并调用 IMissionExecutor 执行；
/// - 每个任务独立 CancellationToken（停止即取消在途执行）；
/// - 记录下次运行时间、上次结果、日志；状态变化通过 Changed 事件通知 UI；
/// - 时钟可注入（单测可确定性推进，无需 sleep）。
/// </summary>
public sealed class MissionScheduler : IDisposable
{
    private readonly MissionStore _store;
    private readonly IMissionExecutor _executor;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _tick;

    private readonly Dictionary<Guid, DateTimeOffset> _nextRun = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _inflight = [];
    private readonly HashSet<Guid> _running = [];
    private readonly List<string> _log = [];
    private Timer? _timer;

    public MissionScheduler(
        MissionStore store,
        IMissionExecutor executor,
        TimeSpan? tickInterval = null,
        Func<DateTimeOffset>? clock = null,
        IMissionProbe? probe = null,
        IMissionRunStore? runStore = null)
    {
        _store = store;
        _executor = executor;
        _tick = tickInterval ?? TimeSpan.FromSeconds(5);
        _clock = clock ?? (() => DateTimeOffset.Now);
        _probe = probe;
        _runStore = runStore;
    }

    private readonly IMissionProbe? _probe;

    /// <summary>
    /// 执行账本（第三阶段补充）：每跑一次就落一条**不截断**的记录，跨重启保留。
    /// 与 <see cref="Log"/> 的区别：Log 是内存滚动日志（重启即空、结果截断），
    /// 账本是"这个任务干过什么"的长期记忆，可查看、可删除。
    /// </summary>
    private readonly IMissionRunStore? _runStore;

    public event Action? Changed;

    public IReadOnlyList<string> Log => _log;
    public bool IsStarted => _timer is not null;
    public const string CapabilityNote =
        "任务会在后台按周期自动巡检：追踪型任务只在对方有新消息时才总结，不会空转；涉及发消息等写操作仍需你确认。" +
        "也可以直接在「对话」页说需求（例如「帮我追踪和小明的聊天，有新消息告诉我」），我会先给出任务草案，你确认后才创建。" +
        "应急刹车：按 Ctrl+Alt+S 可一键停掉所有正在运行的任务（**关闭窗口后依然有效**；"
        + "实际生效的键位以启动日志里那行为准——组合被别的软件占用时会自动换键）。";

    public DateTimeOffset? NextRun(Guid id) => _nextRun.TryGetValue(id, out var t) ? t : null;
    public bool IsRunning(Guid id) => _running.Contains(id);

    /// <summary>正在调度中的任务数（托盘提示/菜单文案用）。</summary>
    public int RunningCount => _running.Count;

    /// <summary>当前是否有一轮执行正在进行（UI 的「终止执行」按钮据此点亮）。</summary>
    public bool IsExecuting(Guid id) => _inflight.ContainsKey(id);

    /// <summary>
    /// 终止**正在进行的那一轮执行**（不是停用任务）：取消在途 CancellationToken，
    /// 让子智能体在当前步骤停下来。任务保持原来的运行/停止状态，下个周期照常巡检。
    ///
    /// 与 <see cref="StopMission"/> 的分工：
    ///   · StopMission = "停用这个任务"（移出巡检 + 取消在途）；
    ///   · CancelRun   = "掐掉这一轮"（任务照旧，只是这一轮不跑了）。
    /// 用户需要的是两者都有：跑飞了要能立刻掐断，而不用先把任务停掉再手动恢复。
    /// </summary>
    public bool CancelRun(Guid id)
    {
        if (!_inflight.TryGetValue(id, out var cts)) return false;
        try { cts.Cancel(); } catch { /* 取消失败也不影响：这一轮最坏就是跑完 */ }
        // 注意：这里**不**把它从 _inflight 移除——真到移除那一步由执行体的 finally 负责。
        // 提前移除会让"同一任务再起一轮"和"上一轮还在收尾"重叠，也会让按钮过早熄灭。
        var title = _store.Find(id)?.Title ?? "（已删除的任务）";
        Append($"已终止「{title}」正在进行的执行（这一轮停在这里，任务状态不变）");
        Notify();
        return true;
    }

    /// <summary>
    /// 启动调度器后台巡检（UI 生命周期内调用一次）。
    ///
    /// **首轮推迟一个周期**（不立刻跑）：这批任务会去操作微信（抢焦点、模拟键鼠），
    /// 一启动就开跑会让用户"刚打开应用就失去操作权"（实测投诉）。
    /// 想马上看结果，用户可以在任务页点「立即执行一次」。
    /// </summary>
    public void Start()
    {
        if (_timer is not null) return;
        // 允许在 Start 前已标记运行中的任务进入巡检，但把首轮排到一个周期之后
        var now = _clock();
        foreach (var m in _store.Items.Where(m => m.Status == MissionStatus.Running))
        {
            _running.Add(m.Id);
            _nextRun[m.Id] = now.Add(Interval(m));
        }
        _timer = new Timer(_ => _ = RunDueAsync(), null, _tick, _tick);
    }

    /// <summary>受理任务：置为运行中并纳入巡检。手动任务不可调度。</summary>
    public bool StartMission(Guid id, bool runImmediately = false)
    {
        var m = _store.Find(id);
        if (m is null || m.Trigger == MissionTriggerKind.Manual) return false;
        m.MarkRunning();
        _running.Add(id);
        // 默认**等一个周期**再跑（见 Start() 的说明）；只有显式要求时才立刻到期
        _nextRun[id] = runImmediately ? _clock() : _clock().Add(Interval(m));
        Append($"已启动「{m.Title}」（{m.TriggerText}）"
               + (runImmediately ? "" : "，首次执行将在下个周期"));
        Notify();
        return true;
    }

    /// <summary>
    /// 一键停掉**所有正在运行的任务**（全局"刹车"键用）：逐个停用并取消在途执行，返回停掉几个。
    /// 覆盖两类：调度中的任务 + "正在跑但已被移出调度"的手动执行。
    /// </summary>
    public int StopAllRunning()
    {
        var ids = _running.ToList();
        foreach (var id in _inflight.Keys.ToList())
            if (!ids.Contains(id)) ids.Add(id);

        foreach (var id in ids) StopMission(id);
        if (ids.Count == 0) Append("全局刹车：当前没有正在运行的任务");
        else Append($"全局刹车：已停止 {ids.Count} 个正在运行的任务");
        return ids.Count;
    }

    /// <summary>停止任务：取消在途执行并移出巡检。</summary>
    public bool StopMission(Guid id)
    {
        var m = _store.Find(id);
        if (m is null) return false;
        if (_inflight.TryGetValue(id, out var cts))
        {
            try { cts.Cancel(); } catch { /* ignore */ }
            _inflight.Remove(id);
        }
        _running.Remove(id);
        _nextRun.Remove(id);
        m.MarkPaused();
        Append($"已停止「{m.Title}」");
        Notify();
        return true;
    }

    /// <summary>执行一次到期的运行中任务（由 Timer 调用，也可在测试中手动驱动）。</summary>
    public async Task RunDueAsync()
    {
        var now = _clock();
        foreach (var m in _store.Items.Where(m => _running.Contains(m.Id) && m.Trigger != MissionTriggerKind.Manual).ToList())
        {
            if (_inflight.ContainsKey(m.Id)) continue;      // 上一轮还在跑
            if (!_nextRun.TryGetValue(m.Id, out var due)) { due = now; }
            if (due > now) continue;

            _nextRun[m.Id] = now.Add(Interval(m));           // 先排下一次，避免执行耗时影响节奏
            await ExecuteOnceAsync(m, auto: true);
        }
    }

    /// <summary>立即手动执行一次（不受间隔限制，不影响调度节奏）。</summary>
    public Task<MissionExecutionResult?> RunOnceAsync(Guid id)
    {
        var m = _store.Find(id);
        if (m is null) return Task.FromResult<MissionExecutionResult?>(null);
        // 已经在跑就别再叠一轮：UI 上连点两下会真的跑两遍（白烧 token / 重复动作），没有任何好处
        if (_inflight.ContainsKey(id)) return Task.FromResult<MissionExecutionResult?>(null);
        return ExecuteOnceInternalAsync(m);
    }

    private async Task<MissionExecutionResult?> ExecuteOnceAsync(MissionDefinition m, bool auto)
    {
        var r = await ExecuteOnceInternalAsync(m);
        if (auto && r is { Success: false })
        {
            m.MarkError(r.Summary);
            Append($"「{m.Title}」执行失败：{r.Summary}");
        }
        Notify();
        return r;
    }

    private async Task<MissionExecutionResult?> ExecuteOnceInternalAsync(MissionDefinition m)
    {
        using var cts = new CancellationTokenSource();
        _inflight[m.Id] = cts;
        Notify();      // 让 UI 立刻知道"这一轮开始了"（「终止执行」按钮这时才点得动）
        try
        {
            // 追踪型任务：先探测"有没有新内容"，没有就跳过，避免空转与重复总结。
            // 注意：跳过**不更新** LastRunAt，否则会把这批新消息漏掉。
            MissionProbeResult? probe = null;
            if (_probe is not null && m.Trigger == MissionTriggerKind.Watch)
            {
                probe = await _probe.ProbeAsync(m, cts.Token);
                if (!probe.ShouldRun)
                {
                    if (m.LastRunAt is null)
                    {
                        // 首次：建立基线，此后只总结"基线之后"的新内容
                        var baseline = $"已建立追踪基线（{probe.Note}），此后只总结新增内容。";
                        m.RecordRun(baseline);
                        Append($"「{m.Title}」{baseline}");
                        RecordRun(m, new MissionExecutionResult { Success = true, Summary = baseline, Skipped = true });
                        return new MissionExecutionResult { Success = true, Summary = baseline, Skipped = true };
                    }

                    var skip = $"本次无新增（{probe.Note}），已跳过。";
                    Append($"「{m.Title}」{skip}");
                    RecordRun(m, new MissionExecutionResult { Success = true, Summary = skip, Skipped = true });
                    return new MissionExecutionResult { Success = true, Summary = skip, Skipped = true };
                }
                Append($"「{m.Title}」探测到新增 {probe.NewCount} 条，开始执行");
            }

            var r = await _executor.ExecuteAsync(m, probe, cts.Token);

            // 被用户终止（「终止执行」）：**不算完成、也不计执行次数**。
            // 子智能体被取消时往往自己收尾返回一句话，看起来像"正常完成"，必须按 token 状态如实区分，
            // 否则任务卡片会把一次被掐断的执行说成干完了（记账与事实不符）。
            if (r.Cancelled || cts.IsCancellationRequested)
            {
                var stopped = r.Cancelled
                    ? r
                    : new MissionExecutionResult { Success = false, Cancelled = true, Summary = r.Summary, ElapsedMs = r.ElapsedMs };
                Append($"「{m.Title}」本轮已被终止（{r.ElapsedMs:0}ms，未计入执行次数）");
                RecordRun(m, stopped);
                return stopped;
            }

            // 卡片上只留一段短摘要（避免 missions.json 被长正文撑大）；**完整正文进账本**（长期记忆）
            m.RecordRun(Truncate(r.Summary, 200));
            Append($"「{m.Title}」执行完成（{r.EvidenceCount} 条证据，{r.ElapsedMs:0}ms）：{Truncate(r.Summary, 60)}");
            RecordRun(m, r);
            return r;
        }
        catch (OperationCanceledException)
        {
            Append($"「{m.Title}」执行已终止（用户点了终止，或任务被停用）");
            return null;
        }
        catch (Exception ex)
        {
            var fail = new MissionExecutionResult { Success = false, Summary = ex.Message };
            m.MarkError(ex.Message);
            Append($"「{m.Title}」异常：{ex.Message}");
            RecordRun(m, fail);
            return fail;
        }
        finally
        {
            _inflight.Remove(m.Id);
            Notify();   // 这一轮真的收尾了 → 按钮熄灭（提前在 CancelRun 里移除会误报"已结束"）
        }
    }

    /// <summary>
    /// 任务的巡检周期：秒级优先（V4.1，"每 30 秒"这类），否则分钟级（最小 1 分钟）。
    /// 注意调度精度受 tick 限制：tick 是 10 秒时，30 秒的任务实际按 ~30 秒节奏触发。
    /// </summary>
    private static TimeSpan Interval(MissionDefinition m) => m.IntervalSpan;

    /// <summary>
    /// 删除任务本身（第三阶段补充）：先取消在途执行、移出巡检，再删任务定义，
    /// **并一并删掉它的执行记录**——记录属于这个任务，留着会变成读不懂的孤儿数据。
    /// </summary>
    public bool RemoveMission(Guid id)
    {
        var m = _store.Find(id);
        if (m is null) return false;
        StopMissionQuietly(id);
        _store.Remove(id);
        _runStore?.Clear(id);
        Append($"已删除任务「{m.Title}」（连同它的执行记录）");
        Notify();
        return true;
    }

    /// <summary>停掉在途执行并移出巡检，但**不写日志、不改状态**（删除流程里用，避免"已停止"噪音）。</summary>
    private void StopMissionQuietly(Guid id)
    {
        if (_inflight.TryGetValue(id, out var cts))
        {
            try { cts.Cancel(); } catch { /* ignore */ }
            _inflight.Remove(id);
        }
        _running.Remove(id);
        _nextRun.Remove(id);
    }

    /// <summary>
    /// 把一次执行写进账本（不截断、跨重启保留）。写失败绝不影响任务执行本身——
    /// 账本是"看得见的记忆"，不是执行的前提。
    /// </summary>
    private void RecordRun(MissionDefinition m, MissionExecutionResult r)
    {
        if (_runStore is null) return;
        try
        {
            _runStore.Append(new MissionRunRecord
            {
                MissionId = m.Id,
                MissionTitle = m.Title,
                At = _clock(),
                Success = r.Success,
                Skipped = r.Skipped,
                Cancelled = r.Cancelled,
                Summary = r.Summary,
                EvidenceCount = r.EvidenceCount,
                ElapsedMs = r.ElapsedMs,
                TraceText = r.TraceText ?? "",
            });
        }
        catch (Exception ex)
        {
            Append($"「{m.Title}」执行记录落盘失败（不影响本次执行）：{ex.Message}");
        }
    }

    private void Append(string line)
    {
        _log.Insert(0, $"[{_clock():HH:mm:ss}] {line}");
        if (_log.Count > 200) _log.RemoveAt(_log.Count - 1);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private void Notify() => Changed?.Invoke();

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        foreach (var cts in _inflight.Values)
        {
            try { cts.Cancel(); } catch { /* ignore */ }
        }
        _inflight.Clear();
        _running.Clear();
        _nextRun.Clear();
    }
}
