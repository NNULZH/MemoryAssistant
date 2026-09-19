using MemoryAssistant.Core.Missions;

namespace MemoryAssistant.Tests;

/// <summary>
/// 第三阶段补充：**任务通知由专用智能体裁决**（值不值得打断用户），而不是见一条弹一条。
///
/// 实测背景：那个"每 40 秒"的任务，一半以上的记录是"跳过/无新增"，
/// 见一条弹一条等于把通知做成骚扰。这组测试钉住：
///   ① 没有信息量的记录（跳过/建基线）根本不进候选；
///   ② 模型的裁决被尊重（说值得才弹，说不值得就不弹）；
///   ③ 模型不可用时退回确定性兜底（失败/终止一定提示），而不是什么都不做；
///   ④ 只播报"上次之后新产生的记录"，不把历史翻出来当新闻重播。
/// </summary>
public sealed class MissionNotificationTests
{
    private static MissionRunRecord Run(
        string title = "就业信息追踪", string summary = "发现 3 条新岗位",
        bool success = true, bool skipped = false, bool cancelled = false,
        DateTimeOffset? at = null, Guid? missionId = null, Guid? id = null)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            MissionId = missionId ?? Guid.NewGuid(),
            MissionTitle = title,
            Summary = summary,
            Success = success,
            Skipped = skipped,
            Cancelled = cancelled,
            At = at ?? DateTimeOffset.Now,
        };

    private sealed class RecordingSink : INotificationSink
    {
        public List<(string Title, string Body)> Sent { get; } = [];
        public void Notify(string title, string body) => Sent.Add((title, body));
    }

    // ---------- 前置过滤：没信息量的不打扰 ----------

    [Fact]
    public void Candidates_SkipTheNoNewsRecords()
    {
        var records = new List<MissionRunRecord>
        {
            Run(summary: "本次无新增（没有新消息提到「就业信息」），已跳过。", skipped: true),
            Run(summary: "已建立追踪基线，此后只总结新增内容。", skipped: true),
            Run(summary: "发现 3 条新岗位"),
            Run(summary: "炸了", success: false),
        };

        var candidates = MissionNotificationAgent.Candidates(records);

        Assert.Equal(2, candidates.Count);
        Assert.DoesNotContain(candidates, r => r.Skipped);
    }

    [Fact]
    public void Candidates_AllSkipped_MeansNothingToSay()
    {
        var records = new List<MissionRunRecord> { Run(skipped: true), Run(skipped: true) };

        Assert.Empty(MissionNotificationAgent.Candidates(records));
    }

    // ---------- 模型裁决 ----------

    private static ScriptedChatClient Chat(string json)
        => new([new MemoryAssistant.Core.Agent.ChatResult { Content = json }]);

    [Fact]
    public async Task Decide_ModelSaysWorth_KeepsItsWording()
    {
        var agent = new MissionNotificationAgent(Chat("""{"worth": true, "title": "有新岗位", "body": "就业群刚贴了 3 个实习岗"}"""));

        var d = await agent.DecideAsync([Run()]);

        Assert.True(d.WorthNotifying);
        Assert.Equal("有新岗位", d.Title);
        Assert.Equal("就业群刚贴了 3 个实习岗", d.Body);
    }

    [Fact]
    public async Task Decide_ModelSaysNotWorth_StaysSilent()
    {
        var agent = new MissionNotificationAgent(Chat("""{"worth": false, "title": "", "body": ""}"""));

        var d = await agent.DecideAsync([Run()]);

        Assert.False(d.WorthNotifying);
    }

    [Fact]
    public async Task Decide_OnlySkippedRecords_NeverCallsModelOrNotifies()
    {
        var chat = Chat("""{"worth": true, "title": "不该出现", "body": "不该出现"}""");
        var agent = new MissionNotificationAgent(chat);

        var d = await agent.DecideAsync([Run(skipped: true)]);

        Assert.False(d.WorthNotifying);
        Assert.Empty(chat.Calls);          // 没有候选就不该花 token
    }

    [Fact]
    public async Task Decide_UnparsableJson_FallsBackToDeterministic()
    {
        var agent = new MissionNotificationAgent(Chat("我不太确定要不要说"));   // 模型不按格式来

        var d = await agent.DecideAsync([Run(summary: "发现 3 条新岗位")]);

        Assert.True(d.WorthNotifying);     // 兜底：至少有内容就提示一句
        Assert.Contains("就业信息追踪", d.Title);
    }

    [Fact]
    public async Task Decide_WithoutModel_StillReportsTrouble()
    {
        // 没配模型也不能把"任务失败了"闷掉
        var d = await new MissionNotificationAgent(chat: null)
            .DecideAsync([Run(summary: "连接失败", success: false)]);

        Assert.True(d.WorthNotifying);
        Assert.Contains("失败", d.Title);
        Assert.Contains("连接失败", d.Body);
    }

    [Fact]
    public async Task Decide_TerminatedRun_IsReportedAsTerminated()
    {
        var d = await new MissionNotificationAgent(chat: null)
            .DecideAsync([Run(summary: "本轮已被终止", cancelled: true)]);

        Assert.True(d.WorthNotifying);
        Assert.Contains("已终止", d.Title);
    }

    // ---------- 游标：只播报"新发生的" ----------

    [Fact]
    public void Cursor_PrimeToNow_SkipsHistory_ThenReportsOnlyNew()
    {
        var store = new InMemoryMissionRunStore();
        var baseTime = DateTimeOffset.Now;
        store.Append(Run(summary: "历史记录", at: baseTime.AddMinutes(-10)));

        var cursor = new MissionRunCursor(store);
        cursor.PrimeToNow();

        Assert.Empty(cursor.NewRecords());                  // 历史不当新闻播

        store.Append(Run(summary: "刚跑完的", at: baseTime));
        var fresh = cursor.NewRecords();

        Assert.Single(fresh);
        Assert.Equal("刚跑完的", fresh[0].Summary);
        Assert.Empty(cursor.NewRecords());                  // 同一条只播一次
    }

    [Fact]
    public void Cursor_ReturnsInTimeOrder()
    {
        var store = new InMemoryMissionRunStore();
        var cursor = new MissionRunCursor(store);
        cursor.PrimeToNow();

        var t0 = DateTimeOffset.Now;
        store.Append(Run(summary: "第二条", at: t0.AddSeconds(2)));
        store.Append(Run(summary: "第一条", at: t0.AddSeconds(1)));

        var fresh = cursor.NewRecords();

        Assert.Equal(["第一条", "第二条"], fresh.Select(r => r.Summary));
    }

    // ---------- 整体：观察者 → 智能体 → 通知出口 ----------

    [Fact]
    public async Task Watcher_NotifiesOnlyWhenAgentSaysWorthIt()
    {
        var store = new InMemoryMissionRunStore();
        var sink = new RecordingSink();
        var cursorWatcher = new MissionNotificationWatcher(
            store,
            new MissionNotificationAgent(Chat("""{"worth": true, "title": "有新岗位", "body": "就业群贴了 3 个实习岗"}""")),
            sink,
            minGap: TimeSpan.Zero);

        store.Append(Run(summary: "发现 3 条新岗位"));
        await cursorWatcher.CheckOnceAsync();

        var sent = Assert.Single(sink.Sent);
        Assert.Equal("有新岗位", sent.Title);
        cursorWatcher.Dispose();
    }

    [Fact]
    public async Task Watcher_MinGapSuppressesBursts()
    {
        // 智能体说"值得"也不能变成轰炸：最小间隔之内只弹第一条
        var store = new InMemoryMissionRunStore();
        var sink = new RecordingSink();
        var audited = new List<string>();
        var watcher = new MissionNotificationWatcher(
            store,
            new MissionNotificationAgent(Chat("""{"worth": true, "title": "有新岗位", "body": "又来了 2 个岗"}""")),
            sink,
            minGap: TimeSpan.FromMinutes(5),
            audit: audited.Add);

        store.Append(Run(summary: "第一条"));
        await watcher.CheckOnceAsync();
        store.Append(Run(summary: "第二条"));
        await watcher.CheckOnceAsync();

        Assert.Single(sink.Sent);
        Assert.Contains(audited, a => a.Contains("距上次通知不足"));
        watcher.Dispose();
    }

    [Fact]
    public async Task Watcher_NoNewRecords_DoesNothing()
    {
        var store = new InMemoryMissionRunStore();
        var sink = new RecordingSink();
        var watcher = new MissionNotificationWatcher(store, new MissionNotificationAgent(chat: null), sink);
        await watcher.CheckOnceAsync();

        Assert.Empty(sink.Sent);
        watcher.Dispose();
    }
}
