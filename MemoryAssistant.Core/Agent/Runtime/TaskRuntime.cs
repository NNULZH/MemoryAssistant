using System.Diagnostics;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.Logging;

namespace MemoryAssistant.Core.Agent.Runtime;

/// <summary>
/// 任务运行器（plan2 §7 控制流骨架）：把 AgentTask 从 Created 驱动到终态。
/// 负责：
///  - 状态可观察（StatusChanged 事件）
///  - 取消向下传播（外部 ct + task.Cancel()）
///  - 任务级超时（MaxTaskSeconds，来自配置）
///  - 把 executor 的产出（AgentResult/结论）回填到 task
/// 不负责具体业务（那是 executor / 后续 Planner + Skill 的事）。
/// </summary>
public sealed class TaskRuntime
{
    private readonly AgentOptions _options;
    private readonly IAppLogger? _logger;

    public TaskRuntime(AgentOptions options, IAppLogger? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<AgentTask> RunAsync(
        AgentTask task,
        IAgentTaskExecutor executor,
        CancellationToken external = default)
    {
        var stopwatch = Stopwatch.StartNew();

        // 超时：把外部取消 + 任务取消合并，并附加 MaxTaskSeconds 计时。
        // 计时用守护任务而不是 CancelAfter：**等用户做决定的时间不计入预算**
        // （见 UserDecisionClock——否则用户晚点几分钟才点确认，他给的批准就被超时吃掉了）。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(external, task.Token);
        using var runOver = new CancellationTokenSource();
        var timed = _options.MaxTaskSeconds > 0;
        if (timed)
            _ = WatchTimeoutAsync(linked, runOver.Token, TimeSpan.FromSeconds(_options.MaxTaskSeconds));
        var ct = linked.Token;

        var budget = new AgentBudget(_options);
        task.Transition(AgentTaskStatus.Planning, $"executor={executor.Name}，预算 工具≤{budget.MaxToolCalls} 失败≤{budget.MaxFailures}");
        task.Transition(AgentTaskStatus.Executing, "开始执行");

        try
        {
            var result = await executor.ExecuteAsync(task, budget, ct);
            task.Result = result;
            task.State.Conclusion = result.Answer;
            stopwatch.Stop();
            task.Transition(AgentTaskStatus.Completed,
                result.CompletedNormally
                    ? $"完成（{result.RoundCount} 轮 / {result.AllToolCalls.Count} 次工具 / {stopwatch.Elapsed.TotalSeconds:0.0}s）"
                    : $"完成（提前终止：{result.EarlyStopReason ?? "未知"}）");
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            if (external.IsCancellationRequested || task.IsCancellationRequested)
            {
                task.Error = "用户取消";
                task.Transition(AgentTaskStatus.Cancelled, "用户取消");
            }
            else
            {
                var reason = timed ? $"超过 MaxTaskSeconds({_options.MaxTaskSeconds})" : "取消";
                task.Error = reason;
                task.Transition(AgentTaskStatus.Failed, $"任务{reason}");
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            task.Error = ex.Message;
            // 打全栈：executor 内部抛错时若不落栈，根因会被吞掉，上层只能看到一句无关的报错。
            _logger?.Error($"[Runtime] 任务执行异常（executor={executor.Name}）：{ex}");
            task.Transition(AgentTaskStatus.Failed, $"执行异常：{ex.Message}");
        }

        return task;
    }

    /// <summary>
    /// 任务级超时的守护：每 250ms 累计一次"智能体真正在干活"的时间，
    /// 期间若 <see cref="UserDecisionClock.IsWaiting"/>（弹窗在等用户），这段就跳过不计。
    /// 预算用完 → 取消本轮（错误信息与原来一致，仍报 MaxTaskSeconds）。
    /// </summary>
    private static async Task WatchTimeoutAsync(
        CancellationTokenSource linked, CancellationToken runOver, TimeSpan limit)
    {
        var stopwatch = Stopwatch.StartNew();
        var last = TimeSpan.Zero;
        var waiting = TimeSpan.Zero;

        while (true)
        {
            try
            {
                await Task.Delay(250, runOver);
            }
            catch (OperationCanceledException)
            {
                return;     // 本轮已结束，守护收工（此时 linked 可能已被 Dispose）
            }

            var now = stopwatch.Elapsed;
            if (UserDecisionClock.IsWaiting) waiting += now - last;
            last = now;

            if (now - waiting < limit) continue;

            try { linked.Cancel(); }
            catch (ObjectDisposedException) { /* 和本轮结束撞车了，无需再取消 */ }
            return;
        }
    }
}
