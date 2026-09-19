using MemoryAssistant.Core.Agent.Action;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Missions;
using MemoryAssistant.Infrastructure.Missions;

namespace MemoryAssistant.Tests;

/// <summary>
/// 第三阶段补充：**任务执行记录 = 长期记忆**（可查看 / 可删除 / 可删任务本身）。
///
/// 实测问题：任务页的"执行历史"是内存日志（重启即空）、正文还被截断到 60 字，
/// 用户想问"上周三那次到底跑了什么"根本查不到。这组测试钉住三件事：
///   ① 每次执行都落一条**不截断**的记录，且跨重启还在；
///   ② 记录可以删单条、可以按任务清空；
///   ③ 删除任务时它的记录一并清掉（不留孤儿数据）。
/// </summary>
public sealed class MissionRunHistoryTests
{
    private sealed class FakeExecutor : IMissionExecutor
    {
        public int Calls { get; private set; }
        public string Summary { get; set; } = "本次结论";

        public Task<MissionExecutionResult> ExecuteAsync(
            MissionDefinition mission, MissionProbeResult? probe, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new MissionExecutionResult
            {
                Success = true,
                Summary = Summary,
                EvidenceCount = 3,
                ElapsedMs = 1234,
                TraceText = "轨迹：规划 → 检索 → 作答",
            });
        }
    }

    private static MissionDefinition Task_(MissionStore store, string title) => store.Add(new MissionDefinition
    {
        Title = title,
        Goal = "g",
        Trigger = MissionTriggerKind.Interval,
        IntervalMinutes = 30,
    });

    // ---------- 账本：追加 / 查看 / 删除 / 清空 ----------

    [Fact]
    public void InMemory_AppendListDeleteClear()
    {
        var store = new InMemoryMissionRunStore();
        var id = Guid.NewGuid();
        store.Append(new MissionRunRecord { MissionId = id, MissionTitle = "A", Summary = "一" });
        store.Append(new MissionRunRecord { MissionId = Guid.NewGuid(), MissionTitle = "B", Summary = "二" });

        Assert.Equal(2, store.List().Count);
        Assert.Single(store.List(id));                       // 按任务筛
        Assert.Equal("B", store.List()[0].MissionTitle);     // 倒序：最新的在前

        var target = store.List(id)[0];
        Assert.True(store.Delete(target.Id));
        Assert.Empty(store.List(id));
        Assert.Equal(1, store.Clear());                      // 剩下的那条一并清掉
        Assert.Empty(store.List());
    }

    [Fact]
    public void Record_KeepsFullText_AndCarriesMeta()
    {
        var longText = new string('长', 500);
        var r = new MissionRunRecord { Summary = longText, EvidenceCount = 4, ElapsedMs = 888, Success = true };

        Assert.Equal(500, r.Summary.Length);                 // **不截断**：可查看指的就是它
        Assert.Contains("完成", r.MetaText);
        Assert.Contains("4 条证据", r.MetaText);
        Assert.Equal(longText[..80] + "…", r.Preview);       // 列表行才截断
    }

    [Fact]
    public void Record_SkippedIsNotFailure()
    {
        var r = new MissionRunRecord { Skipped = true, Success = true, Summary = "本次无新增，已跳过。" };

        Assert.Equal("跳过（无新增）", r.StatusText);
    }

    // ---------- 落盘：跨重启还在 ----------

    [Fact]
    public void Json_RoundTripsAcrossInstances_AndDeletesPersist()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ma-runs-{Guid.NewGuid():N}.json");
        try
        {
            var id = Guid.NewGuid();
            var first = new JsonMissionRunStore(path);
            first.Append(new MissionRunRecord { MissionId = id, MissionTitle = "秋招监控", Summary = "发现 3 条新岗位" });
            var keepId = first.List(id)[0].Id;

            // 新实例 = 重启后的进程：记录必须还在（这是"长期记忆"的定义）
            var second = new JsonMissionRunStore(path);
            var loaded = second.List(id);
            Assert.Single(loaded);
            Assert.Equal("发现 3 条新岗位", loaded[0].Summary);

            Assert.True(second.Delete(keepId));
            Assert.Empty(new JsonMissionRunStore(path).List(id));      // 删除也要落盘
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Json_CorruptFile_IsTreatedAsEmpty_NotACrash()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ma-runs-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ 这不是合法 JSON");
            Assert.Empty(new JsonMissionRunStore(path).List());        // 坏文件不能让任务页打不开
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---------- 调度器：每次执行都记账 ----------

    [Fact]
    public async Task Scheduler_RecordsEachRun_WithFullResult()
    {
        var store = new MissionStore();
        var m = Task_(store, "定时巡检");
        var runs = new InMemoryMissionRunStore();
        var exec = new FakeExecutor { Summary = new string('详', 300) };
        var scheduler = new MissionScheduler(store, exec, runStore: runs);

        await scheduler.RunOnceAsync(m.Id);
        await scheduler.RunOnceAsync(m.Id);

        var records = runs.List(m.Id);
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal("定时巡检", r.MissionTitle));
        Assert.All(records, r => Assert.Equal(300, r.Summary.Length));   // 账本里是全文
        Assert.Contains(records, r => r.TraceText.Length > 0);           // 轨迹也留下来
    }

    [Fact]
    public async Task Scheduler_FailedRun_IsRecordedAsFailure()
    {
        var store = new MissionStore();
        var m = Task_(store, "会失败的任务");
        var runs = new InMemoryMissionRunStore();
        var scheduler = new MissionScheduler(store, new ThrowingExecutor(), runStore: runs);

        await scheduler.RunOnceAsync(m.Id);

        var r = Assert.Single(runs.List(m.Id));
        Assert.False(r.Success);
        Assert.Contains("炸了", r.Summary);
    }

    private sealed class ThrowingExecutor : IMissionExecutor
    {
        public Task<MissionExecutionResult> ExecuteAsync(
            MissionDefinition mission, MissionProbeResult? probe, CancellationToken ct)
            => throw new InvalidOperationException("炸了");
    }

    [Fact]
    public async Task Scheduler_RemoveMission_DropsTaskAndItsRecords()
    {
        var store = new MissionStore();
        var m = Task_(store, "要被删掉的任务");
        var runs = new InMemoryMissionRunStore();
        var scheduler = new MissionScheduler(store, new FakeExecutor(), runStore: runs);
        await scheduler.RunOnceAsync(m.Id);
        Assert.Single(runs.List(m.Id));

        Assert.True(scheduler.RemoveMission(m.Id));

        Assert.Null(store.Find(m.Id));                 // 任务没了
        Assert.Empty(runs.List(m.Id));                 // 记录也没了（不留孤儿数据）
        Assert.DoesNotContain(scheduler.Log, l => l.Contains("已停止", StringComparison.Ordinal));
    }

    // ---------- Agent 也读得到这份长期记忆 ----------

    [Fact]
    public async Task Tool_ListMissionRuns_ShowsRecordsToTheModel()
    {
        var store = new MissionStore();
        var m = Task_(store, "就业信息追踪");
        var runs = new InMemoryMissionRunStore();
        var scheduler = new MissionScheduler(store, new FakeExecutor(), runStore: runs);
        await scheduler.RunOnceAsync(m.Id);

        var registry = new ToolRegistry();
        new MissionToolProvider(store, scheduler, new AlwaysApproveActionConfirmation(), () => { }, runs: runs)
            .RegisterAll(registry);

        Assert.True(registry.TryGet("list_mission_runs", out var tool));
        var all = await tool.ExecuteAsync("{}", CancellationToken.None);
        Assert.True(all.Success);
        Assert.Contains("就业信息追踪", all.Output);

        var one = await tool.ExecuteAsync("""{"name":"就业信息追踪"}""", CancellationToken.None);
        Assert.True(one.Success);
        Assert.Contains("本次结论", one.Output);

        var none = await tool.ExecuteAsync("""{"name":"不存在的任务"}""", CancellationToken.None);
        Assert.False(none.Success);      // 名字对不上要如实说，不要瞎编记录
    }
}
