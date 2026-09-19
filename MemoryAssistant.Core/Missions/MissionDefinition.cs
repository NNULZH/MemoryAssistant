namespace MemoryAssistant.Core.Missions;

/// <summary>任务触发方式（V3 任务模型）。</summary>
public enum MissionTriggerKind
{
    Manual,    // 手动触发一次
    Interval,  // 定时（每 N 分钟）
    Watch,     // 常驻监听（如"追踪与某人的聊天"）
}

/// <summary>任务运行状态。</summary>
public enum MissionStatus
{
    Paused,   // 已停止/未启动
    Running,  // 运行中（调度器托管）
    Error,    // 上次执行失败
}

/// <summary>
/// 任务的动作类型（V3.7 自动回复）：
/// 触发方式（何时跑）与动作（跑完做什么）分开，调度/探测逻辑不用为"自动回复"改一遍。
/// </summary>
public enum MissionActionKind
{
    /// <summary>产出总结/汇报（默认，历史行为）。</summary>
    Summarize = 0,
    /// <summary>产出并自动发送回复（写操作：仅回复"对方发来的新消息"）。</summary>
    AutoReply = 1,
}

/// <summary>
/// 任务定义（V3 Mission）：把"随机需求"固化为可重复执行的明确目标。
/// V3.1 只做模型 + 状态；真实调度/执行在 V3.2 接入。
/// </summary>
public sealed class MissionDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    /// <summary>明确目标（给 Agent 的自然语言任务描述）。</summary>
    public string Goal { get; set; } = "";
    public MissionTriggerKind Trigger { get; set; } = MissionTriggerKind.Manual;
    /// <summary>跑完做什么：总结（默认）还是自动回复（写操作，见 <see cref="MissionActionKind"/>）。</summary>
    public MissionActionKind Action { get; set; } = MissionActionKind.Summarize;
    /// <summary>Interval/Watch 的间隔分钟数（Watch 可理解为巡检间隔）。</summary>
    public int IntervalMinutes { get; set; } = 30;

    /// <summary>
    /// 秒级间隔（V4.1）：&gt;0 时**优先于** <see cref="IntervalMinutes"/>，用于"每 30 秒"这类高频巡检
    /// （自动回复/智能聊天需要秒级节奏，分钟级表达不出来）。
    /// 单开一个字段而不是改分钟字段的语义，是为了不动已有 missions.json 里"分钟"的含义。
    /// </summary>
    public int IntervalSeconds { get; set; }

    /// <summary>实际巡检周期：秒级优先；分钟级保持原来的"最小 1 分钟"下限。</summary>
    public TimeSpan IntervalSpan => IntervalSeconds > 0
        ? TimeSpan.FromSeconds(IntervalSeconds)
        : TimeSpan.FromMinutes(Math.Max(1, IntervalMinutes));
    /// <summary>
    /// 追踪对象（V3.6）：Watch 型任务盯着的会话对象（联系人昵称/群名）；为空则退化为"按目标整段执行"。
    /// </summary>
    public string Target { get; set; } = "";
    public MissionStatus Status { get; private set; } = MissionStatus.Paused;
    /// <summary>写操作（发消息/回复）是否需要人工确认（V3 安全策略：默认需要）。</summary>
    public bool RequiresApproval { get; set; } = true;
    public DateTimeOffset? LastRunAt { get; private set; }
    public string? LastResult { get; private set; }

    /// <summary>
    /// 累计执行次数（课程展示用：卡片上"执行次数"）。
    /// 只统计"真的跑过一次并留下结果"的次数，跨重启由仓库恢复。
    /// </summary>
    public int RunCount { get; private set; }

    /// <summary>
    /// 主题监听的长期画像（V4.1，JSON）：模型扩出来的相关词 + 各会话命中累计。
    /// 只对"目标不是会话"的主题型 Watch 任务有意义（普通任务恒为空串）。
    /// 放在任务上持久化，"这个群常聊就业"才能跨轮次/跨重启留下来。
    /// </summary>
    public string WatchProfile { get; private set; } = "";

    public string StatusText => Status switch
    {
        MissionStatus.Running => "运行中",
        MissionStatus.Error => "异常",
        _ => "已停止",
    };

    /// <summary>动作标记（自动回复是写操作，必须在任务列表里一眼看见）。</summary>
    public string ActionText => Action == MissionActionKind.AutoReply ? "自动回复" : "总结";

    /// <summary>
    /// 创建方式（第三阶段 §23）：agent = Agent 根据自然语言创建；user = 用户在任务页手工填写。
    /// 这个字段的展示价值在于——"这个任务不是用户手工填的配置，而是 Agent 自己理解出来的"。
    /// </summary>
    public string Origin { get; set; } = "";

    public const string OriginAgent = "agent";
    public const string OriginUser = "user";

    /// <summary>创建方式（人话）。空值（早期数据）按手工创建展示。</summary>
    public string OriginText => Origin == OriginAgent ? "Agent 创建" : "手工创建";

    /// <summary>状态点用的状态键（UI 配 StepStateToBrush 上色：running / failed / pending）。</summary>
    public string StatusState => Status switch
    {
        MissionStatus.Running => "running",
        MissionStatus.Error => "failed",
        _ => "pending",
    };

    /// <summary>上次执行时间（人话）。</summary>
    public string LastRunAtText => LastRunAt is null
        ? "尚未执行"
        : LastRunAt.Value.ToString("MM-dd HH:mm:ss");

    /// <summary>执行次数（课程展示：让"它真的在后台干活"看得见）。</summary>
    public string RunCountText => $"{RunCount} 次";

    public string TriggerText => (Trigger switch
    {
        MissionTriggerKind.Interval => MissionText.Interval(IntervalMinutes, IntervalSeconds),
        MissionTriggerKind.Watch => string.IsNullOrWhiteSpace(Target)
            ? $"常驻监听（{MissionText.Interval(IntervalMinutes, IntervalSeconds)}巡检）"
            : $"追踪「{Target}」（{MissionText.Interval(IntervalMinutes, IntervalSeconds)}巡检）",
        _ => "手动",
    }) + (Action == MissionActionKind.AutoReply ? " · 自动回复" : "");

    public void MarkRunning()
    {
        Status = MissionStatus.Running;
        // 运行态不覆盖"上次执行结果"（历史结果保留，便于用户回看）
    }

    public void MarkPaused(string? note = null)
    {
        Status = MissionStatus.Paused;
        // 停止不应抹掉"上次执行结果"——只在显式给 note 时更新
        if (!string.IsNullOrWhiteSpace(note)) LastResult = note;
    }

    public void MarkError(string error)
    {
        Status = MissionStatus.Error;
        LastResult = error;
    }

    public void RecordRun(string result)
    {
        LastRunAt = DateTimeOffset.Now;
        LastResult = result;
        RunCount++;
    }

    /// <summary>UI/无障碍显示用（避免自动化树里出现类型全名）。</summary>
    public override string ToString() => string.IsNullOrWhiteSpace(Title) ? Goal : Title;

    /// <summary>写回本轮主题监听的画像（空值忽略，避免把已有画像抹掉）。</summary>
    public void RecordWatchProfile(string? profileJson)
    {
        if (!string.IsNullOrWhiteSpace(profileJson)) WatchProfile = profileJson;
    }

    /// <summary>从持久化恢复运行态字段（仅仓库加载时使用）。</summary>
    public void Restore(MissionStatus status, DateTimeOffset? lastRunAt, string? lastResult, string? watchProfile = null, int runCount = 0, string? origin = null)
    {
        Status = status;
        LastRunAt = lastRunAt;
        LastResult = lastResult;
        RunCount = runCount;
        if (!string.IsNullOrWhiteSpace(origin)) Origin = origin!;
        if (!string.IsNullOrWhiteSpace(watchProfile)) WatchProfile = watchProfile;
    }
}

/// <summary>任务存储（V3.2：支持从仓库加载后再构造）。</summary>
public sealed class MissionStore
{
    private readonly List<MissionDefinition> _items = [];

    public MissionStore()
    {
    }

    public MissionStore(IEnumerable<MissionDefinition> items)
    {
        _items.AddRange(items);
    }

    public IReadOnlyList<MissionDefinition> Items => _items;

    public MissionDefinition Add(MissionDefinition m)
    {
        _items.Add(m);
        return m;
    }

    public bool Remove(Guid id)
    {
        var m = _items.FirstOrDefault(x => x.Id == id);
        return m is not null && _items.Remove(m);
    }

    public MissionDefinition? Find(Guid id) => _items.FirstOrDefault(x => x.Id == id);

    /// <summary>示例任务（骨架期展示用，说明"任务可以被固定下来"）。</summary>
    public static MissionStore WithSamples()
    {
        var store = new MissionStore();
        store.Add(new MissionDefinition
        {
            Title = "追踪与「某人」的聊天",
            Goal = "持续关注与指定联系人的聊天，出现新消息时总结要点；如需回复，先给出草稿等我确认。",
            Trigger = MissionTriggerKind.Watch,
            IntervalMinutes = 15,
        });
        store.Add(new MissionDefinition
        {
            Title = "每日汇总未完成承诺",
            Goal = "每天整理我答应过但尚未完成的事情，列出待确认清单。",
            Trigger = MissionTriggerKind.Interval,
            IntervalMinutes = 1440,
        });
        return store;
    }
}
