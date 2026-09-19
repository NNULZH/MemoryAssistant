namespace MemoryAssistant.Core.Missions;

/// <summary>
/// 任务草案（对话式编排）：把用户的一句自然语言需求翻译成"可执行的任务配置"。
/// 草案在用户确认前**不落库、不调度**——"理解需求"与"真的创建"分开，避免误建。
/// </summary>
public sealed record MissionDraft
{
    public string Title { get; init; } = "";
    /// <summary>用户原始需求（完整保留，供执行器理解"要做什么"）。</summary>
    public string Goal { get; init; } = "";
    public MissionTriggerKind Trigger { get; init; } = MissionTriggerKind.Manual;
    /// <summary>跑完做什么：总结（默认）还是自动回复（写操作，创建后不再逐条确认）。</summary>
    public MissionActionKind Action { get; init; } = MissionActionKind.Summarize;
    public int IntervalMinutes { get; init; } = 30;
    /// <summary>秒级间隔（V4.1）：&gt;0 时优先于分钟（"每 30 秒"）。</summary>
    public int IntervalSeconds { get; init; }
    /// <summary>追踪对象（Watch 型必填；其余为空）。自动回复必须填——否则不知道该回给谁。</summary>
    public string Target { get; init; } = "";
    /// <summary>Agent 为什么这样理解（给人看的理由，便于用户纠错）。</summary>
    public string Reason { get; init; } = "";

    /// <summary>触发方式的人话描述（复用 MissionDefinition 的措辞规则）。</summary>
    public string TriggerText => (Trigger switch
    {
        MissionTriggerKind.Interval => MissionText.Interval(IntervalMinutes, IntervalSeconds),
        MissionTriggerKind.Watch => string.IsNullOrWhiteSpace(Target)
            ? $"常驻监听（{MissionText.Interval(IntervalMinutes, IntervalSeconds)}巡检）"
            : $"追踪「{Target}」（{MissionText.Interval(IntervalMinutes, IntervalSeconds)}巡检）",
        _ => "手动执行",
    }) + (Action == MissionActionKind.AutoReply ? " · 自动回复" : "");

    /// <summary>确认卡正文：让用户在创建前看清"要创建什么、什么时候跑、跑什么"。</summary>
    public string Describe()
    {
        var lines = new List<string>
        {
            $"任务名称：{Title}",
            $"触发方式：{TriggerText}",
            $"任务目标：{Goal}",
        };
        if (Action == MissionActionKind.AutoReply)
            lines.Add("⚠ 这个任务会**自动发送**微信消息（不再逐条确认）；只回复「对方发来的新消息」。" +
                      "写操作可随时在该任务上点「停止」，或把配置里的自动回复刹车打开。");
        if (!string.IsNullOrWhiteSpace(Reason)) lines.Add($"理解依据：{Reason}");
        return string.Join("\n", lines);
    }

    /// <summary>转成真正可调度的任务定义。</summary>
    public MissionDefinition ToDefinition() => new()
    {
        Title = Title,
        Goal = Goal,
        Trigger = Trigger,
        Action = Action,
        IntervalMinutes = IntervalMinutes,
        IntervalSeconds = IntervalSeconds,
        // 自动回复没有 Trigger=Watch 时也要保留对象（否则不知道该回给谁）
        Target = Trigger == MissionTriggerKind.Watch || Action == MissionActionKind.AutoReply ? Target : "",
        // 自动回复的"人工确认"发生在创建这一刻（确认卡上写明了会自动发送），运行期不再逐条问。
        RequiresApproval = true,
    };
}
