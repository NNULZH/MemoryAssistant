using MemoryAssistant.Core.Missions;

namespace MemoryAssistant.Infrastructure.Missions;

/// <summary>
/// 占位任务执行器：Bridge 未就绪（如 Python 环境缺失）时使用。
/// 让任务功能"降级但不消失"——任务仍可创建与查看，执行时如实报告不可用，不静默失败。
/// </summary>
public sealed class UnavailableMissionExecutor : IMissionExecutor
{
    public Task<MissionExecutionResult> ExecuteAsync(
        MissionDefinition mission, MissionProbeResult? probe, CancellationToken ct)
        => Task.FromResult(new MissionExecutionResult
        {
            Success = false,
            Summary = "执行环境未就绪（Python Bridge 未启动），本次任务未执行。",
        });
}
