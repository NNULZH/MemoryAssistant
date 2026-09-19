using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Planner;
using MemoryAssistant.Core.Agent.Runtime;
using MemoryAssistant.Core.Agent.Skills;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.Missions;
using MemoryAssistant.Core.RAG;
using MemoryAssistant.Infrastructure.Missions;

namespace MemoryAssistant.Tests;

/// <summary>
/// V4.0 对话式任务编排单测：一句话需求 → 任务草案 → （确认后）落库 + 纳入调度。
/// 全链路零 LLM / 零数据：解析器规则确定性、Skill 与管道用 FakeBackend。
/// </summary>
public sealed class MissionComposeTests
{
    // ---------- 需求解析（文本 → 可执行任务配置） ----------

    /// <summary>
    /// V4.1：用户要的"每 30 秒和指定对象智能聊天，AI 决定是否回复"必须能解析成
    /// **定时（秒级）+ 自动回复 + 明确对象**——这是"用文字指令建任务"的核心用例。
    /// </summary>
    [Fact]
    public void Parse_Every30SecondsSmartChat_BecomesAutoReplyIntervalMission()
    {
        var d = MissionIntentParser.TryParse("每30秒和张晓明智能聊天，AI决定是否回复，回复什么")!;

        Assert.NotNull(d);
        Assert.Equal(MissionActionKind.AutoReply, d.Action);
        Assert.Equal(MissionTriggerKind.Interval, d.Trigger);   // 写了周期词 → 定时扫描，而不是只在新消息时
        Assert.Equal("张晓明", d.Target);
        Assert.Equal(30, d.IntervalSeconds);                      // 秒级，不能被"最小 1 分钟"吃掉
        Assert.Equal(0, d.IntervalMinutes);
        Assert.Contains("每 30 秒", d.TriggerText);
        Assert.Contains("自动回复", d.TriggerText);
    }

    /// <summary>秒级周期的各种写法都要认（阿拉伯数字 / s 简写 / 中文数字）。</summary>
    [Theory]
    [InlineData("每30秒和张晓明智能聊天，AI决定是否回复", 30)]
    [InlineData("每30s和张晓明智能聊天", 30)]
    [InlineData("每三十秒跟张晓明智能聊天", 30)]
    [InlineData("每隔15秒和张晓明自动聊天", 15)]
    public void Parse_SecondsInterval_IsRecognized(string query, int seconds)
    {
        var d = MissionIntentParser.TryParse(query);

        Assert.NotNull(d);
        Assert.Equal(seconds, d!.IntervalSeconds);
        Assert.Equal("张晓明", d.Target);
    }

    /// <summary>回归护栏：裸"聊天"绝不能把只读的追踪任务升级成写操作（自动回复）。</summary>
    [Fact]
    public void Parse_TrackChat_IsStillReadOnlyWatch()
    {
        var d = MissionIntentParser.TryParse("帮我追踪和张晓明的聊天，有新消息就总结给我")!;

        Assert.NotNull(d);
        Assert.Equal(MissionActionKind.Summarize, d.Action);
        Assert.Equal(MissionTriggerKind.Watch, d.Trigger);
        Assert.Equal(0, d.IntervalSeconds);
    }

    [Fact]
    public void Parse_TrackVerb_BecomesWatchMission()
    {
        var d = MissionIntentParser.TryParse("帮我追踪和文件传输助手的聊天，有新消息就总结给我");
        Assert.NotNull(d);
        Assert.Equal(MissionTriggerKind.Watch, d!.Trigger);
        Assert.Equal("文件传输助手", d.Target);
        Assert.Equal(15, d.IntervalMinutes);              // 未指定周期 → 默认 15 分钟巡检
        Assert.Contains("追踪", d.Title);
        Assert.Equal("帮我追踪和文件传输助手的聊天，有新消息就总结给我", d.Goal); // 原始需求完整保留
    }

    [Fact]
    public void Parse_TrackVerb_WithExplicitInterval()
    {
        var d = MissionIntentParser.TryParse("每30分钟帮我盯一下小明的聊天");
        Assert.NotNull(d);
        Assert.Equal(MissionTriggerKind.Watch, d!.Trigger);
        Assert.Equal("小明", d.Target);
        Assert.Equal(30, d.IntervalMinutes);
    }

    [Fact]
    public void Parse_DailyReminder_BecomesIntervalMission()
    {
        var d = MissionIntentParser.TryParse("每天帮我整理一遍未完成的承诺，提醒我");
        Assert.NotNull(d);
        Assert.Equal(MissionTriggerKind.Interval, d!.Trigger);
        Assert.Equal(1440, d.IntervalMinutes);
        Assert.Equal("", d.Target);                        // 非追踪型不写追踪对象
    }

    [Theory]
    [InlineData("每周帮我汇总一次大家聊了什么", 10080)]
    [InlineData("每小时看看有没有人找我借钱", 60)]
    [InlineData("每隔10分钟提醒我一次", 10)]
    [InlineData("每十分钟提醒我一次", 10)]
    public void Parse_PeriodWords_MapToMinutes(string query, int minutes)
    {
        var d = MissionIntentParser.TryParse(query);
        Assert.NotNull(d);
        Assert.Equal(minutes, d!.IntervalMinutes);
    }

    [Fact]
    public void Parse_ExplicitMissionWord_BecomesManualMission()
    {
        var d = MissionIntentParser.TryParse("帮我建个任务，整理一下我这周的聊天");
        Assert.NotNull(d);
        Assert.Equal(MissionTriggerKind.Manual, d!.Trigger);
    }

    [Theory]
    [InlineData("我每天都聊天吗")]                       // 疑问句 → 不是派活
    [InlineData("我每天几点才开始聊天")]                 // 疑问词 → 不是派活
    [InlineData("我最近是不是经常深夜聊天")]
    [InlineData("我和小明上周聊过什么")]
    [InlineData("我还有哪些答应过的事没做")]
    [InlineData("帮我看看和小王的聊天记录里有没有提到秋招")]  // 弱动词"关注/看看"不得触发建任务
    [InlineData("我留意到他最近没说话")]                 // 弱动词"留意"单独出现（陈述句）不得建任务
    public void Parse_OrdinaryQuestions_AreNotMissions(string query)
    {
        Assert.Null(MissionIntentParser.TryParse(query));
    }

    /// <summary>
    /// V4.1：「新增一个 loop，可以留意就业信息」——显式编排词（新增一个）+ 弱动词（留意）同现时，
    /// 要建出一个"常驻监听 + 盯住这个主题"的任务（触发方式=Watch，对象=就业信息）。
    /// </summary>
    [Fact]
    public void Parse_AddLoopWatchTopic_BecomesWatchMission()
    {
        var d = MissionIntentParser.TryParse("新增一个loop，可以留意就业信息");

        Assert.NotNull(d);
        Assert.Equal(MissionTriggerKind.Watch, d!.Trigger);       // loop → 常驻监听，不是一次性手动任务
        Assert.Equal(MissionActionKind.Summarize, d.Action);      // 只读：只看不回复
        Assert.Equal("就业信息", d.Target);
        Assert.Contains("就业信息", d.Title);
        Assert.Equal(15, d.IntervalMinutes);                      // 未写周期 → 追踪型默认每 15 分钟巡检
    }

    [Fact]
    public void Draft_DescribeAndToDefinition_AreConsistent()
    {
        var d = MissionIntentParser.TryParse("追踪小明的聊天")!;
        var text = d.Describe();
        Assert.Contains("任务名称：", text);
        Assert.Contains(d.TriggerText, text);

        var def = d.ToDefinition();
        Assert.Equal(MissionTriggerKind.Watch, def.Trigger);
        Assert.Equal("小明", def.Target);
        Assert.True(def.RequiresApproval);                 // 写操作默认需人工确认
    }

    // ---------- Skill 与 Agent 管道 ----------

    private sealed class FakeBackend : IMemoryBackend
    {
        public Task<IReadOnlyList<RetrievedChunk>> SemanticSearchAsync(string q, int topK, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RetrievedChunk>>([]);
        public Task<IReadOnlyList<Evidence>> ReadDayMessagesAsync(string s, string d, int l, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<IReadOnlyList<Evidence>> KeywordSearchAsync(string k, string? s, int l, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<string> GetStatsTextAsync(int l, int s, CancellationToken ct) => Task.FromResult("");
        public Task<IReadOnlyList<DayActivity>> GetRecentDaysAsync(int c, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DayActivity>>([]);
        public Task<IReadOnlyList<Evidence>> GetDaySnippetsAsync(string d, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<IReadOnlyList<Evidence>> ScanCommitmentsAsync(int l, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Evidence>>([]);
        public Task<string> GetTopicsTextAsync(int d, CancellationToken ct) => Task.FromResult("");
        public Task<string> GetProfilesTextAsync(int t, CancellationToken ct) => Task.FromResult("");
    }

    [Fact]
    public void RulePlanner_RoutesMissionRequests_ToMissionSkill()
    {
        var plan = RulePlanner.RulePlan("帮我追踪小明的聊天，有新消息告诉我", hint: null);
        Assert.Single(plan.Steps);
        Assert.Equal("mission", plan.Steps[0].Name);
        Assert.Equal(PlanStepKind.Skill, plan.Steps[0].Kind);
    }

    [Fact]
    public async Task MissionSkill_ProducesDraftWithoutSideEffect()
    {
        var skill = new MissionSkill();
        var r = await skill.ExecuteAsync(new SkillRequest { Query = "追踪小明的聊天" }, CancellationToken.None);

        Assert.True(r.Success);
        Assert.True(r.Sufficient);                          // 一步到位 → 不触发重规划
        Assert.NotNull(r.PendingMission);
        Assert.Equal("小明", r.PendingMission!.Target);
        Assert.Empty(r.Evidence);                           // 只理解需求，不查数据、不落库
    }

    [Fact]
    public async Task MissionSkill_Unparsable_ReportsHonestly()
    {
        var skill = new MissionSkill();
        var r = await skill.ExecuteAsync(new SkillRequest { Query = "我和小明聊过什么" }, CancellationToken.None);

        Assert.True(r.Success);
        Assert.False(r.Sufficient);
        Assert.Null(r.PendingMission);
    }

    /// <summary>
    /// 回归护栏：Skill 必须解析**用户原话**，不能用规划器重述过的 goal。
    /// 实测（GUI）"每30秒和文件传输助手智能聊天…"被规划器重述成"创建一个每 30 秒轮询文件传输助手会话…"，
    /// 重述句里没了"和X智能聊天"这个句式，规则解析就把结尾的"任务"当成了回复对象。
    /// </summary>
    [Fact]
    public async Task MissionSkill_PrefersOriginalQuery_OverRewrittenGoal()
    {
        var skill = new MissionSkill();
        var r = await skill.ExecuteAsync(new SkillRequest
        {
            // 规划器重述后的 goal（Query）
            Query = "创建一个每 30 秒轮询文件传输助手会话、由 AI 判断是否需要回复及回复内容的智能聊天任务",
            // 用户原话
            OriginalQuery = "每30秒和文件传输助手智能聊天，AI决定是否回复，回复什么",
        }, CancellationToken.None);

        Assert.NotNull(r.PendingMission);
        Assert.Equal("文件传输助手", r.PendingMission!.Target);       // 不是"任务"
        Assert.Equal(30, r.PendingMission.IntervalSeconds);
        Assert.Equal(MissionActionKind.AutoReply, r.PendingMission.Action);
        Assert.Equal(MissionTriggerKind.Interval, r.PendingMission.Trigger);
    }

    [Fact]
    public async Task AgentPipeline_SurfacesPendingMission_OnAgentResult()
    {
        var opts = new AgentOptions { MaxToolCalls = 10, MaxTaskSeconds = 0 };
        var planner = new Planner(SkillCatalog.Default(), opts, chat: null);
        var skills = SkillRegistry.BuildDefault(new FakeBackend(), extra: [new MissionSkill()]);
        var orchestrator = new AgentOrchestrator(planner, skills, opts);
        var task = new AgentTask("帮我追踪小明的聊天，有新消息就告诉我");
        var rt = new TaskRuntime(opts);

        var done = await rt.RunAsync(task, orchestrator, CancellationToken.None);
        var result = done.Result!;

        Assert.True(result.CompletedNormally);
        Assert.NotNull(result.PendingMission);              // UI 据此渲染"任务确认卡"
        Assert.Equal("小明", result.PendingMission!.Target);
        Assert.Equal(MissionTriggerKind.Watch, result.PendingMission.Trigger);
    }

    [Fact]
    public async Task AgentPipeline_NormalQuery_HasNoPendingMission()
    {
        var opts = new AgentOptions { MaxToolCalls = 10, MaxTaskSeconds = 0 };
        var planner = new Planner(SkillCatalog.Default(), opts, chat: null);
        var skills = SkillRegistry.BuildDefault(new FakeBackend(), extra: [new MissionSkill()]);
        var orchestrator = new AgentOrchestrator(planner, skills, opts);
        var task = new AgentTask("我和小明上周聊过什么");
        var rt = new TaskRuntime(opts);

        var done = await rt.RunAsync(task, orchestrator, CancellationToken.None);
        Assert.Null(done.Result!.PendingMission);
    }

    // ---------- 写侧：确认后才创建（落库 + 持久化 + 纳管） ----------

    [Fact]
    public void Composer_Create_WritesPersistsAndSchedules()
    {
        var store = new MissionStore();
        var repo = new JsonMissionRepository(
            Path.Combine(Path.GetTempPath(), $"compose_{Guid.NewGuid():N}.json"));
        var scheduler = new MissionScheduler(store, new NoopExecutor());
        int saved = 0;
        var composer = new MissionComposer(store, scheduler, () => { saved++; repo.Save(store.Items); });

        var draft = MissionIntentParser.TryParse("追踪小明的聊天")!;
        var created = composer.Create(draft, start: true);

        Assert.Single(composer.Catalog);
        Assert.Equal(MissionStatus.Running, created.Status);          // 已纳入调度
        Assert.NotNull(scheduler.NextRun(created.Id));
        Assert.True(saved > 0);
        Assert.True(File.Exists(repo.FilePath));
        Assert.Equal("小明", repo.Load().Single().Target);            // 追踪对象持久化

        scheduler.Dispose();
        try { File.Delete(repo.FilePath); } catch { /* ignore */ }
    }

    [Fact]
    public void Composer_CreateManual_DoesNotSchedule()
    {
        var store = new MissionStore();
        var scheduler = new MissionScheduler(store, new NoopExecutor());
        var composer = new MissionComposer(store, scheduler, () => { });

        var created = composer.Create(
            new MissionDraft { Title = "手动任务", Goal = "g", Trigger = MissionTriggerKind.Manual },
            start: true);

        Assert.Equal(MissionStatus.Paused, created.Status);           // 手动任务不参与自动调度
        Assert.Null(scheduler.NextRun(created.Id));
        scheduler.Dispose();
    }

    private sealed class NoopExecutor : IMissionExecutor
    {
        public Task<MissionExecutionResult> ExecuteAsync(
            MissionDefinition mission, MissionProbeResult? probe, CancellationToken ct)
            => Task.FromResult(new MissionExecutionResult { Success = true, Summary = "ok" });
    }
}
