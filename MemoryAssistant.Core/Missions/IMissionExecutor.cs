namespace MemoryAssistant.Core.Missions;

/// <summary>一次任务执行的产出（写回任务日志，供 UI 展示）。</summary>
public sealed record MissionExecutionResult
{
    public bool Success { get; init; }
    public string Summary { get; init; } = "";
    public int EvidenceCount { get; init; }
    /// <summary>执行轨迹文本（TaskTrace.ToText()），用于任务详情/日志展开。</summary>
    public string? TraceText { get; init; }
    public double ElapsedMs { get; init; }
    /// <summary>本次是否因"没有新内容"被跳过（V3.6 追踪型任务）。</summary>
    public bool Skipped { get; init; }

    /// <summary>
    /// 本次是否被**用户终止**（任务页的「终止执行」）。
    ///
    /// 为什么要单列：子智能体被取消时不一定抛异常——它可能自己收尾并回一句"没能查完（用户取消）"，
    /// 于是调度器那边看起来像"正常完成"。若把它记成"完成"，任务卡片上的执行次数与"最近结果"
    /// 就会把一次被掐断的执行说成干完了（记账必须与真实发生的事一致）。
    /// </summary>
    public bool Cancelled { get; init; }
}

/// <summary>
/// 任务执行器：由上层（App）用 ConversationalAgent 实现——任务目标 → Agent 自主执行。
/// Core 只定义契约，保证调度器与具体 Agent 解耦、可单测。
/// </summary>
public interface IMissionExecutor
{
    /// <summary>
    /// 执行一次任务。<paramref name="probe"/> 为本次增量探测结果（追踪型任务才有），
    /// 执行器应把其中的增量上下文并入任务输入（例如"只针对这批新消息总结"）。
    /// </summary>
    Task<MissionExecutionResult> ExecuteAsync(
        MissionDefinition mission, MissionProbeResult? probe, CancellationToken ct);
}

/// <summary>
/// 追踪型任务执行前的"有没有活儿"探测（V3.6）。
/// 例：追踪某人的聊天 → 探测自上次执行以来该会话是否有新消息；没有就跳过本次执行，避免空转与重复总结。
/// Core 只定义契约（实现放 App/Infrastructure，通过 Bridge 读真实数据）。
/// </summary>
public interface IMissionProbe
{
    Task<MissionProbeResult> ProbeAsync(MissionDefinition mission, CancellationToken ct);
}

/// <summary>一次探测的结果。</summary>
public sealed record MissionProbeResult
{
    /// <summary>是否值得执行（false = 没有新内容，跳过）。</summary>
    public bool ShouldRun { get; init; } = true;
    /// <summary>本次增量条数（如新消息数）。</summary>
    public int NewCount { get; init; }
    /// <summary>人话说明（写进任务日志，如"目标会话无新消息"）。</summary>
    public string Note { get; init; } = "";
    /// <summary>增量上下文文本（新消息内容），供执行器并入任务输入。</summary>
    public string Context { get; init; } = "";

    public static MissionProbeResult Run(string note = "", int newCount = 0, string context = "")
        => new() { ShouldRun = true, Note = note, NewCount = newCount, Context = context };

    public static MissionProbeResult Skip(string note)
        => new() { ShouldRun = false, Note = note };
}

/// <summary>任务持久化契约（实现放 Infrastructure，Core 不碰文件系统）。</summary>
public interface IMissionRepository
{
    IReadOnlyList<MissionDefinition> Load();
    void Save(IEnumerable<MissionDefinition> missions);
}
