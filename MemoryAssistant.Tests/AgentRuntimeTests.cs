using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Runtime;
using MemoryAssistant.Core.Configuration;

namespace MemoryAssistant.Tests;

/// <summary>
/// P11 Agent Runtime 单测：用 fake executor（零 LLM / 零数据）验证
/// 状态机可观察性、取消、超时、失败、预算耗尽、步骤/观测写回。
/// </summary>
public sealed class AgentRuntimeTests
{
    private sealed class FakeExecutor(
        string name,
        Func<AgentTask, AgentBudget, CancellationToken, Task<AgentResult>> body) : IAgentTaskExecutor
    {
        public string Name => name;
        public Task<AgentResult> ExecuteAsync(AgentTask task, AgentBudget budget, CancellationToken ct)
            => body(task, budget, ct);
    }

    private static AgentOptions NoTimeout()
    {
        var opts = new AgentOptions();
        opts.MaxTaskSeconds = 0; // 默认 120s 对单测过长，取消任务级超时
        return opts;
    }

    // ---- P11-C：Run 可观察，正常路径状态迁移 + 结论回填 ----

    [Fact]
    public async Task Normal_Completes_WithObservableTransitions()
    {
        var task = new AgentTask("我最近和谁聊工作？");
        var seen = new List<AgentTaskStatus>();
        task.StatusChanged += (_, e) => seen.Add(e.Status);

        var rt = new TaskRuntime(NoTimeout());
        var done = await rt.RunAsync(task, new FakeExecutor("recall", async (t, b, ct) =>
        {
            var step = t.State.AddStep("recall", "语义召回").MarkRunning();
            await Task.Delay(5, ct);
            step.Complete("命中 1 个会话");
            b.TryConsumeTool();
            t.State.AddEvidence(new Evidence { Index = 1, Content = "原文", SessionId = "s1" });
            return new AgentResult { Answer = "答案是 X", CompletedNormally = true, RoundCount = 1 };
        }));

        Assert.Equal(AgentTaskStatus.Completed, done.Status);
        Assert.NotNull(done.Result);
        Assert.Equal("答案是 X", done.Result!.Answer);
        Assert.Equal("答案是 X", done.State.Conclusion);
        Assert.Single(done.State.Steps);
        Assert.Equal(TaskStepStatus.Completed, done.State.Steps[0].Status);
        Assert.Single(done.State.Evidence);
        Assert.NotNull(done.StartedAt);
        Assert.NotNull(done.FinishedAt);
        // 观察到至少 Planning → Executing → Completed
        Assert.Contains(AgentTaskStatus.Planning, seen);
        Assert.Contains(AgentTaskStatus.Executing, seen);
        Assert.Equal(AgentTaskStatus.Completed, seen[^1]);
    }

    // ---- P11-D：取消向下传播 ----

    [Fact]
    public async Task Cancel_Propagates_ToCancelled()
    {
        var task = new AgentTask("取消");
        var rt = new TaskRuntime(NoTimeout());
        var run = rt.RunAsync(task, new FakeExecutor("block", async (_, _, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new AgentResult();
        }), CancellationToken.None);

        await Task.Delay(50);
        Assert.False(task.IsCancellationRequested);
        task.Cancel();
        Assert.True(task.IsCancellationRequested);

        var done = await run;
        Assert.Equal(AgentTaskStatus.Cancelled, done.Status);
        Assert.Equal("用户取消", done.Error);
        Assert.NotNull(done.FinishedAt);
    }

    // ---- P11-D：超时（MaxTaskSeconds） ----

    [Fact]
    public async Task Timeout_WhenExceedsMaxTaskSeconds()
    {
        var task = new AgentTask("超时");
        var rt = new TaskRuntime(new AgentOptions { MaxTaskSeconds = 1 });
        var done = await rt.RunAsync(task, new FakeExecutor("slow", async (_, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new AgentResult();
        }));

        Assert.Equal(AgentTaskStatus.Failed, done.Status);
        Assert.NotNull(done.Error);
        Assert.Contains("MaxTaskSeconds", done.Error);
    }

    // ---- 回归：等用户做决定的时间不算进任务预算 ----
    // 实测踩过：确认窗开了 8 分钟才被点，任务早已"超过 MaxTaskSeconds"，用户点头之后什么都没发生。
    [Fact]
    public async Task Timeout_DoesNotCountTimeSpentWaitingForUserDecision()
    {
        var task = new AgentTask("等用户确认");
        var rt = new TaskRuntime(new AgentOptions { MaxTaskSeconds = 1 });
        var done = await rt.RunAsync(task, new FakeExecutor("confirm", async (_, _, _) =>
        {
            // 弹窗期间（1.6s > 预算 1s）计时应当暂停，回到未暂停状态后正常跑完
            using (UserDecisionClock.Wait()) await Task.Delay(1600);
            await Task.Delay(200);
            return new AgentResult { Answer = "已发送" };
        }));

        Assert.Equal(AgentTaskStatus.Completed, done.Status);
        Assert.Equal("已发送", done.State.Conclusion);
    }

    // ---- 暂停必须成对：Dispose 后计数要回到 0，否则超时以后永远不再生效 ----
    [Fact]
    public void UserDecisionClock_Wait_IsIdempotentAndRestoresState()
    {
        var scope = UserDecisionClock.Wait();
        Assert.True(UserDecisionClock.IsWaiting);
        scope.Dispose();
        scope.Dispose();     // 重复 Dispose 不能让计数掉到 0 以下
        Assert.False(UserDecisionClock.IsWaiting);
    }

    // ---- P11-D：executor 抛异常 → Failed + Error ----

    [Fact]
    public async Task Exception_MarksFailed_WithError()
    {
        var task = new AgentTask("失败");
        var rt = new TaskRuntime(NoTimeout());
        var done = await rt.RunAsync(task, new FakeExecutor("boom", (_, _, _) =>
            throw new InvalidOperationException("Bridge 不可用")));

        Assert.Equal(AgentTaskStatus.Failed, done.Status);
        Assert.Equal("Bridge 不可用", done.Error);
        Assert.NotNull(done.FinishedAt);
    }

    // ---- P11-D：工具预算耗尽 ----

    [Fact]
    public async Task BudgetExhausted_ExecutorStops_HonestlyReports()
    {
        var task = new AgentTask("预算");
        var opts = new AgentOptions { MaxToolCalls = 3, MaxTaskSeconds = 0 };
        var rt = new TaskRuntime(opts);
        var done = await rt.RunAsync(task, new FakeExecutor("looper", async (t, b, ct) =>
        {
            while (b.TryConsumeTool())
                await Task.Delay(5, ct);
            t.State.TerminationReason = "tool_budget_exceeded";
            return new AgentResult { Answer = "预算用尽。", CompletedNormally = false, EarlyStopReason = "tool_budget_exceeded" };
        }));

        Assert.Equal(AgentTaskStatus.Completed, done.Status); // runtime 层面正常收尾
        Assert.False(done.Result!.CompletedNormally);
        Assert.Equal("tool_budget_exceeded", done.Result.EarlyStopReason);
        Assert.Equal("tool_budget_exceeded", done.State.TerminationReason);
    }

    // ---- P11：失败预算计数 ----

    [Fact]
    public void Budget_TracksFailures()
    {
        var budget = new AgentBudget(new AgentOptions { MaxFailures = 2, MaxToolCalls = 10 });
        Assert.False(budget.FailuresExhausted);
        budget.RegisterFailure();
        budget.RegisterFailure();
        Assert.True(budget.FailuresExhausted);
    }

    // ---- P11-A：Scratchpad 摘要与 TaskState 记录 ----

    [Fact]
    public void Scratchpad_RecordsWorkingNotes()
    {
        var state = new TaskState("谁常深夜找我聊天？");
        state.Goal = "找出最近一个月最常深夜聊天的人";
        state.Scratchpad.Goal = "找出最近一个月最常深夜聊天的人";
        state.Scratchpad.AddKnown("范围=最近30天");
        state.Scratchpad.AddMissing("还缺深夜消息数");
        state.Scratchpad.SetNext("query stats", "query timeline");
        state.AddObservation(new Observation { Source = "stats", Summary = "top 会话已拿到" });

        var text = state.Scratchpad.ToSummary();
        Assert.Contains("goal:", text);
        Assert.Contains("known:", text);
        Assert.Contains("missing:", text);
        Assert.Contains("next:", text);
        Assert.Single(state.Observations);
        Assert.Equal("query stats", state.Scratchpad.NextActions[0]);
    }
}
