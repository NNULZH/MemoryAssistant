namespace MemoryAssistant.Core.Agent.Runtime;

/// <summary>
/// 一次可观测的结果：来自某个 Skill / Tool 的执行产物。
/// 只放摘要 + 截断详情，避免把模型/工具的原始大输出塞进任务状态。
/// </summary>
public sealed record Observation
{
    public string Source { get; init; } = "";        // skill/tool 名称
    public string Summary { get; init; } = "";       // 一句话摘要
    public string Detail { get; init; } = "";        // 截断后的详情
    public bool Success { get; init; } = true;
    public double LatencyMs { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>关联证据（EvidenceStore 中的 Id，P16 后使用）。</summary>
    public string? EvidenceRef { get; init; }
}
