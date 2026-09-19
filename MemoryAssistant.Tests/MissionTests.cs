using MemoryAssistant.Core.Missions;
using MemoryAssistant.Infrastructure.Missions;

namespace MemoryAssistant.Tests;

/// <summary>V3.2 任务系统单测：模型/状态机/真实调度（时钟注入）/取消/持久化（零 LLM/零数据）。</summary>
public sealed class MissionTests
{
    // ---------- 模型 ----------

    [Fact]
    public void Samples_ArePausedByDefault()
    {
        var store = MissionStore.WithSamples();
        Assert.Equal(2, store.Items.Count);
        Assert.All(store.Items, m => Assert.Equal(MissionStatus.Paused, m.Status));
        Assert.All(store.Items, m => Assert.True(m.RequiresApproval)); // 写操作默认需确认
    }

    [Fact]
    public void TriggerText_DescribesKind()
    {
        var store = new MissionStore();
        var manual = store.Add(new MissionDefinition { Title = "t", Trigger = MissionTriggerKind.Manual });
        var interval = store.Add(new MissionDefinition { Title = "t", Trigger = MissionTriggerKind.Interval, IntervalMinutes = 10 });
        var watch = store.Add(new MissionDefinition { Title = "t", Trigger = MissionTriggerKind.Watch, IntervalMinutes = 5 });

        Assert.Equal("手动", manual.TriggerText);
        Assert.Contains("10", interval.TriggerText);
        Assert.Contains("常驻", watch.TriggerText);
        Assert.Equal("t", manual.ToString());
    }

    // ---------- 调度器（时钟注入，确定性） ----------

    private sealed class FakeExecutor : IMissionExecutor
    {
        public int Calls { get; private set; }
        public bool WaitUntilCancelled { get; set; }
        public ManualResetEventSlim Started { get; } = new(false);
        public bool ObservedCancel { get; private set; }
        public Func<int, string> SummaryFor { get; set; } = n => $"第{n}次执行完成";
        public bool Fail { get; set; }

        public async Task<MissionExecutionResult> ExecuteAsync(
            MissionDefinition mission, MissionProbeResult? probe, CancellationToken ct)
        {
            Calls++;
            Started.Set();
            if (WaitUntilCancelled)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) { ObservedCancel = true; throw; }
            }
            if (Fail)
                return new MissionExecutionResult { Success = false, Summary = "模拟失败" };
            return new MissionExecutionResult { Success = true, Summary = SummaryFor(Calls), EvidenceCount = 2 };
        }
    }

    [Fact]
    public async Task Scheduler_RunsDueTask_AndRespectsInterval()
    {
        var clock = DateTimeOffset.Parse("2026-09-08T10:00:00+08:00");
        var store = new MissionStore();
        var m = store.Add(new MissionDefinition { Title = "巡检", Goal = "g", Trigger = MissionTriggerKind.Interval, IntervalMinutes = 30 });
        var exec = new FakeExecutor();
        var scheduler = new MissionScheduler(store, exec, clock: () => clock);

        Assert.True(scheduler.StartMission(m.Id, runImmediately: true)); // 立即到期
        Assert.Equal(MissionStatus.Running, m.Status);
        Assert.NotNull(scheduler.NextRun(m.Id));

        await scheduler.RunDueAsync();
        Assert.Equal(1, exec.Calls);
        Assert.Equal("第1次执行完成", m.LastResult);
        Assert.NotNull(m.LastRunAt);

        await scheduler.RunDueAsync();           // 同一时刻再巡检：未到期
        Assert.Equal(1, exec.Calls);

        clock = clock.AddMinutes(30);            // 推进到下一次
        await scheduler.RunDueAsync();
        Assert.Equal(2, exec.Calls);
    }

    /// <summary>
    /// V4.1：秒级间隔必须真的按秒推进。"每 30 秒"这类任务如果被"最小 1 分钟"的下限吃掉，
    /// 用户看到的节奏就完全不对（而且这一点只能靠可注入时钟确定性地测出来）。
    /// </summary>
    [Fact]
    public async Task Scheduler_HonoursSecondsInterval()
    {
        var clock = DateTimeOffset.Parse("2026-09-13T10:00:00+08:00");
        var store = new MissionStore();
        var m = store.Add(new MissionDefinition
        {
            Title = "每30秒智能聊天",
            Goal = "g",
            Trigger = MissionTriggerKind.Interval,
            Action = MissionActionKind.AutoReply,
            IntervalMinutes = 0,
            IntervalSeconds = 30,
            Target = "张晓明",
        });
        var exec = new FakeExecutor();
        var scheduler = new MissionScheduler(store, exec, clock: () => clock);

        Assert.Equal(TimeSpan.FromSeconds(30), m.IntervalSpan);
        Assert.True(scheduler.StartMission(m.Id, runImmediately: true));   // 立即到期

        await scheduler.RunDueAsync();
        Assert.Equal(1, exec.Calls);

        clock = clock.AddSeconds(29);                // 差 1 秒：不该跑
        await scheduler.RunDueAsync();
        Assert.Equal(1, exec.Calls);

        clock = clock.AddSeconds(1);                 // 满 30 秒：该跑
        await scheduler.RunDueAsync();
        Assert.Equal(2, exec.Calls);
    }

    [Fact]
    public async Task Scheduler_Stop_CancelsInflightExecution()
    {
        var store = new MissionStore();
        var m = store.Add(new MissionDefinition { Title = "长任务", Goal = "g", Trigger = MissionTriggerKind.Watch, IntervalMinutes = 5 });
        var exec = new FakeExecutor { WaitUntilCancelled = true };
        var scheduler = new MissionScheduler(store, exec);

        scheduler.StartMission(m.Id, runImmediately: true);
        var running = scheduler.RunDueAsync();
        Assert.True(exec.Started.Wait(TimeSpan.FromSeconds(2)));

        scheduler.StopMission(m.Id);
        await running.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(exec.ObservedCancel);
        Assert.Equal(MissionStatus.Paused, m.Status);
        Assert.False(scheduler.IsRunning(m.Id));
        Assert.Null(scheduler.NextRun(m.Id));
    }

    // ---------- 终止"正在进行的那一轮"（与"停用任务"分开） ----------

    [Fact]
    public async Task Scheduler_CancelRun_StopsThisRoundButKeepsTaskRunning()
    {
        var store = new MissionStore();
        var m = store.Add(new MissionDefinition { Title = "长任务", Goal = "g", Trigger = MissionTriggerKind.Interval, IntervalMinutes = 5 });
        var exec = new FakeExecutor { WaitUntilCancelled = true };
        var scheduler = new MissionScheduler(store, exec);

        scheduler.StartMission(m.Id, runImmediately: true);
        var running = scheduler.RunDueAsync();
        Assert.True(exec.Started.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(scheduler.IsExecuting(m.Id));       // 正在跑 → 「终止执行」按钮可点

        Assert.True(scheduler.CancelRun(m.Id));         // 掐掉这一轮
        await running.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(exec.ObservedCancel);
        Assert.False(scheduler.IsExecuting(m.Id));      // 真收尾了 → 按钮熄灭
        Assert.Equal(MissionStatus.Running, m.Status);  // 关键：任务仍"运行中"（只是这一轮被掐断）
        Assert.NotNull(scheduler.NextRun(m.Id));        // 下个周期照常巡检
    }

    [Fact]
    public void Scheduler_CancelRun_WithoutInflightRun_ReturnsFalse()
    {
        var scheduler = new MissionScheduler(new MissionStore(), new FakeExecutor());

        Assert.False(scheduler.CancelRun(Guid.NewGuid()));
    }

    [Fact]
    public async Task RunOnceAsync_DoesNotStackASecondRun()
    {
        var store = new MissionStore();
        var m = store.Add(new MissionDefinition { Title = "手动", Goal = "g" });
        var exec = new FakeExecutor { WaitUntilCancelled = true };
        var scheduler = new MissionScheduler(store, exec);

        var first = scheduler.RunOnceAsync(m.Id);
        Assert.True(exec.Started.Wait(TimeSpan.FromSeconds(2)));

        var second = await scheduler.RunOnceAsync(m.Id);   // 连点第二下：不该再叠一轮（白烧 token）
        Assert.Null(second);
        Assert.Equal(1, exec.Calls);

        scheduler.CancelRun(m.Id);
        await first.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Scheduler_KeepsShortResultOnTheCard_ButFullTextInTheRecord()
    {
        var store = new MissionStore();
        var m = store.Add(new MissionDefinition { Title = "长结论", Goal = "g", Trigger = MissionTriggerKind.Interval, IntervalMinutes = 5 });
        var runs = new InMemoryMissionRunStore();
        var exec = new FakeExecutor { SummaryFor = _ => new string('详', 500) };
        var scheduler = new MissionScheduler(store, exec, runStore: runs);

        await scheduler.RunOnceAsync(m.Id);

        Assert.Equal(201, m.LastResult!.Length);            // 卡片/missions.json 里只留短摘要（200 + …）
        Assert.Equal(500, runs.List(m.Id)[0].Summary.Length);  // 账本里是完整正文
    }

    [Fact]
    public async Task Scheduler_TerminatedRun_IsRecordedAsCancelled_AndNotCounted()
    {
        // 子智能体被取消时常常自己收尾、返回一个"正常"结果（"这次没能查完：用户取消"），
        // 不能因此把它记成"完成"——否则卡片上的执行次数与"最近结果"都在撒谎
        var store = new MissionStore();
        var m = store.Add(new MissionDefinition { Title = "被终止", Goal = "g", Trigger = MissionTriggerKind.Interval, IntervalMinutes = 5 });
        var runs = new InMemoryMissionRunStore();
        var scheduler = new MissionScheduler(store, new CancelledExecutor(), runStore: runs);

        await scheduler.RunOnceAsync(m.Id);

        Assert.Equal(0, m.RunCount);                          // 被掐断的一轮不计执行次数
        var rec = Assert.Single(runs.List(m.Id));
        Assert.Equal("已终止", rec.StatusText);               // 账本如实写"已终止"
        Assert.Contains("已被终止", scheduler.Log[0]);
    }

    private sealed class CancelledExecutor : IMissionExecutor
    {
        public Task<MissionExecutionResult> ExecuteAsync(
            MissionDefinition mission, MissionProbeResult? probe, CancellationToken ct)
            => Task.FromResult(new MissionExecutionResult
            {
                Success = true,
                Cancelled = true,
                Summary = "这次没能查完：用户取消。",
            });
    }

    // ---------- 启动应用时不抢操作权 + 全局刹车 ----------

    [Fact]
    public async Task Start_DoesNotRunTasksImmediately_FirstRunWaitsOneInterval()
    {
        // 实测投诉：一启动应用任务就开跑（会抢焦点、模拟键鼠），用户"刚打开就失去操作权"
        var clock = DateTimeOffset.Parse("2026-09-14T10:00:00+08:00");
        var store = new MissionStore();
        var m = store.Add(new MissionDefinition
        {
            Title = "定时任务", Goal = "g", Trigger = MissionTriggerKind.Interval, IntervalMinutes = 30,
        });
        m.MarkRunning();
        var exec = new FakeExecutor();
        var scheduler = new MissionScheduler(store, exec, clock: () => clock);

        scheduler.Start();
        await scheduler.RunDueAsync();

        Assert.Equal(0, exec.Calls);                        // 启动那一刻**不跑**
        Assert.NotNull(scheduler.NextRun(m.Id));            // 但已经排好了首轮时间

        clock = clock.AddMinutes(31);                        // 过一个周期
        await scheduler.RunDueAsync();
        Assert.Equal(1, exec.Calls);
    }

    [Fact]
    public async Task StartMission_DefaultWaitsOneInterval_UnlessExplicitlyImmediate()
    {
        var clock = DateTimeOffset.Parse("2026-09-14T10:00:00+08:00");
        var store = new MissionStore();
        var m = store.Add(new MissionDefinition
        {
            Title = "定时任务", Goal = "g", Trigger = MissionTriggerKind.Interval, IntervalMinutes = 10,
        });
        var exec = new FakeExecutor();
        var scheduler = new MissionScheduler(store, exec, clock: () => clock);

        scheduler.StartMission(m.Id);                       // 默认：等一个周期
        await scheduler.RunDueAsync();
        Assert.Equal(0, exec.Calls);

        clock = clock.AddMinutes(11);
        await scheduler.RunDueAsync();
        Assert.Equal(1, exec.Calls);
    }

    [Fact]
    public void StopAllRunning_StopsEveryRunningTask_AndReportsCount()
    {
        var store = new MissionStore();
        var a = store.Add(new MissionDefinition { Title = "A", Goal = "g", Trigger = MissionTriggerKind.Interval, IntervalMinutes = 5 });
        var b = store.Add(new MissionDefinition { Title = "B", Goal = "g", Trigger = MissionTriggerKind.Interval, IntervalMinutes = 5 });
        var c = store.Add(new MissionDefinition { Title = "C", Goal = "g", Trigger = MissionTriggerKind.Interval, IntervalMinutes = 5 });
        var scheduler = new MissionScheduler(store, new FakeExecutor());
        scheduler.StartMission(a.Id);
        scheduler.StartMission(b.Id);
        // C 不启动

        var stopped = scheduler.StopAllRunning();

        Assert.Equal(2, stopped);
        Assert.Equal(MissionStatus.Paused, a.Status);
        Assert.Equal(MissionStatus.Paused, b.Status);
        Assert.False(scheduler.IsRunning(a.Id));
        Assert.Equal(MissionStatus.Paused, c.Status);       // 没在跑的本来就不会被算进去
        Assert.Contains(scheduler.Log, l => l.Contains("全局刹车：已停止 2 个正在运行的任务"));
    }

    [Fact]
    public void StopAllRunning_WithNothingRunning_SaysSoInsteadOfPretending()
    {
        var scheduler = new MissionScheduler(new MissionStore(), new FakeExecutor());

        Assert.Equal(0, scheduler.StopAllRunning());
        Assert.Contains(scheduler.Log, l => l.Contains("当前没有正在运行的任务"));
    }

    [Fact]
    public async Task Scheduler_Failure_MarksError()
    {
        var store = new MissionStore();
        var m = store.Add(new MissionDefinition { Title = "会失败", Goal = "g", Trigger = MissionTriggerKind.Interval, IntervalMinutes = 5 });
        var scheduler = new MissionScheduler(store, new FakeExecutor { Fail = true });
        scheduler.StartMission(m.Id, runImmediately: true);
        await scheduler.RunDueAsync();
        Assert.Equal(MissionStatus.Error, m.Status);
    }

    [Fact]
    public async Task RunOnceAsync_WorksWithoutScheduling()
    {
        var store = new MissionStore();
        var m = store.Add(new MissionDefinition { Title = "手动", Goal = "g" });
        var exec = new FakeExecutor();
        var scheduler = new MissionScheduler(store, exec);

        var r = await scheduler.RunOnceAsync(m.Id);
        Assert.NotNull(r);
        Assert.Equal(1, exec.Calls);
        Assert.Equal("第1次执行完成", m.LastResult);
    }

    // ---------- V3.6 追踪型任务：先探测增量，没有新内容就跳过 ----------

    private sealed class FakeProbe : IMissionProbe
    {
        public int Calls { get; private set; }
        public Func<MissionDefinition, MissionProbeResult> ResultFor { get; set; } = _ => MissionProbeResult.Skip("无新消息");

        public Task<MissionProbeResult> ProbeAsync(MissionDefinition mission, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(ResultFor(mission));
        }
    }

    private sealed class RecordingExecutor : IMissionExecutor
    {
        public int Calls { get; private set; }
        public MissionProbeResult? LastProbe { get; private set; }

        public Task<MissionExecutionResult> ExecuteAsync(
            MissionDefinition mission, MissionProbeResult? probe, CancellationToken ct)
        {
            Calls++;
            LastProbe = probe;
            return Task.FromResult(new MissionExecutionResult { Success = true, Summary = "总结完成" });
        }
    }

    [Fact]
    public async Task WatchMission_FirstRunWithNoMessages_EstablishesBaselineOnly()
    {
        var store = new MissionStore();
        var m = store.Add(new MissionDefinition
        {
            Title = "追踪",
            Goal = "总结新消息",
            Trigger = MissionTriggerKind.Watch,
            Target = "某人",
        });
        var exec = new RecordingExecutor();
        var probe = new FakeProbe { ResultFor = _ => MissionProbeResult.Skip("尚无历史消息可作基线") };
        var scheduler = new MissionScheduler(store, exec, probe: probe);

        var r = await scheduler.RunOnceAsync(m.Id);

        Assert.NotNull(r);
        Assert.True(r!.Skipped);
        Assert.Equal(0, exec.Calls);                 // 没有内容就不空跑
        Assert.NotNull(m.LastRunAt);                 // 但要把基线定下来
        Assert.Contains("基线", m.LastResult);
    }

    [Fact]
    public async Task WatchMission_NoNewSinceBaseline_SkippedWithoutTouchingBaseline()
    {
        var store = new MissionStore();
        var m = store.Add(new MissionDefinition
        {
            Title = "追踪",
            Goal = "总结新消息",
            Trigger = MissionTriggerKind.Watch,
            Target = "某人",
        });
        m.RecordRun("基线");                               // 已有基线
        var exec = new RecordingExecutor();
        var probe = new FakeProbe { ResultFor = _ => MissionProbeResult.Skip("目标会话没有对方的新消息") };
        var scheduler = new MissionScheduler(store, exec, probe: probe);

        var r = await scheduler.RunOnceAsync(m.Id);

        Assert.True(r!.Skipped);
        Assert.Equal(0, exec.Calls);
        Assert.Equal("基线", m.LastResult);            // 跳过不覆盖历史结果
    }

    [Fact]
    public async Task WatchMission_WithNewMessages_PassesDeltaToExecutor()
    {
        var store = new MissionStore();
        var m = store.Add(new MissionDefinition
        {
            Title = "追踪",
            Goal = "总结新消息",
            Trigger = MissionTriggerKind.Watch,
            Target = "某人",
        });
        var exec = new RecordingExecutor();
        var probe = new FakeProbe
        {
            ResultFor = _ => MissionProbeResult.Run("新增 3 条", 3, "[09:00] 某人: 在吗"),
        };
        var scheduler = new MissionScheduler(store, exec, probe: probe);

        var r = await scheduler.RunOnceAsync(m.Id);

        Assert.False(r!.Skipped);
        Assert.Equal(1, exec.Calls);
        Assert.Equal(3, exec.LastProbe!.NewCount);
        Assert.Contains("在吗", exec.LastProbe.Context);   // 增量上下文原样传给执行器
        Assert.Equal("总结完成", m.LastResult);
    }

    [Fact]
    public async Task IntervalMission_DoesNotConsultProbe()
    {
        var store = new MissionStore();
        var m = store.Add(new MissionDefinition
        {
            Title = "每日汇总",
            Goal = "g",
            Trigger = MissionTriggerKind.Interval,
            IntervalMinutes = 60,
        });
        var exec = new RecordingExecutor();
        var probe = new FakeProbe { ResultFor = _ => MissionProbeResult.Skip("不该被问到") };
        var scheduler = new MissionScheduler(store, exec, probe: probe);

        var r = await scheduler.RunOnceAsync(m.Id);

        Assert.False(r!.Skipped);
        Assert.Equal(1, exec.Calls);
        Assert.Equal(0, probe.Calls);                  // 定时任务不走向量探测
        Assert.Null(exec.LastProbe);
    }

    [Fact]
    public async Task UnknownId_ReturnsFalseOrNull()
    {
        var scheduler = new MissionScheduler(new MissionStore(), new FakeExecutor());
        Assert.False(scheduler.StartMission(Guid.NewGuid()));
        Assert.False(scheduler.StopMission(Guid.NewGuid()));
        Assert.Null(await scheduler.RunOnceAsync(Guid.NewGuid()));
    }

    // ---------- 持久化 ----------

    [Fact]
    public void JsonRepository_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missions_{Guid.NewGuid():N}.json");
        try
        {
            var repo = new JsonMissionRepository(path);
            var store = new MissionStore();
            var m = store.Add(new MissionDefinition
            {
                Title = "追踪某人",
                Goal = "持续关注与某人的聊天",
                Trigger = MissionTriggerKind.Watch,
                IntervalMinutes = 15,
                Target = "文件传输助手",
            });
            m.MarkRunning();
            m.RecordRun("第一次执行：发现 3 条新消息");
            repo.Save(store.Items);

            var loaded = repo.Load();
            var l = Assert.Single(loaded);
            Assert.Equal("追踪某人", l.Title);
            Assert.Equal(MissionTriggerKind.Watch, l.Trigger);
            Assert.Equal(15, l.IntervalMinutes);
            Assert.Equal("文件传输助手", l.Target);
            Assert.Equal(MissionStatus.Running, l.Status);
            Assert.Equal("第一次执行：发现 3 条新消息", l.LastResult);
            Assert.NotNull(l.LastRunAt);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>V4.1：秒级间隔也要能落盘/读回——否则重启后"每 30 秒"会退回 30 分钟。</summary>
    [Fact]
    public void JsonRepository_RoundTripsSecondsInterval()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missions_sec_{Guid.NewGuid():N}.json");
        try
        {
            var repo = new JsonMissionRepository(path);
            var store = new MissionStore();
            store.Add(new MissionDefinition
            {
                Title = "每30秒智能聊天",
                Goal = "每30秒和张晓明智能聊天，AI决定是否回复",
                Trigger = MissionTriggerKind.Interval,
                Action = MissionActionKind.AutoReply,
                IntervalMinutes = 0,
                IntervalSeconds = 30,
                Target = "张晓明",
            });
            repo.Save(store.Items);

            var l = Assert.Single(repo.Load());
            Assert.Equal(30, l.IntervalSeconds);
            Assert.Equal(TimeSpan.FromSeconds(30), l.IntervalSpan);   // 秒级优先，不被分钟下限吃掉
            Assert.Equal(MissionActionKind.AutoReply, l.Action);
            Assert.Equal("张晓明", l.Target);
            Assert.Contains("每 30 秒", l.TriggerText);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void JsonRepository_MissingFile_ReturnsEmpty()
    {
        var repo = new JsonMissionRepository(Path.Combine(Path.GetTempPath(), $"nope_{Guid.NewGuid():N}.json"));
        Assert.Empty(repo.Load());
    }
}
