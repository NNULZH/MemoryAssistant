using MemoryAssistant.Core.Agent;

namespace MemoryAssistant.Core.Agent.Runtime;

/// <summary>
/// 任务运行态：目标、计划、已观测结果、证据、工具调用次数、终止原因。
/// 与聊天历史（AgentContext）分离；单个任务的全部中间产物都在这里（plan2 §3.2）。
/// </summary>
public sealed class TaskState
{
    public TaskState(string userQuery)
    {
        UserQuery = userQuery;
    }

    public string UserQuery { get; }
    public string Goal { get; set; } = "";
    public Scratchpad Scratchpad { get; } = new();

    public List<AgentTaskStep> Steps { get; } = [];
    public List<Observation> Observations { get; } = [];
    public List<Evidence> Evidence { get; } = [];
    public int ToolCallCount { get; set; }
    public string? CurrentStepId { get; set; }
    public string? Conclusion { get; set; }
    public string? TerminationReason { get; set; }
    /// <summary>本轮理解出的任务草案（对话式编排；由 Skill 产出，交 UI 确认）。</summary>
    public Missions.MissionDraft? PendingMission { get; set; }

    public AgentTaskStep AddStep(string kind, string name, string? reason = null)
    {
        var step = new AgentTaskStep { Kind = kind, Name = name, Reason = reason };
        Steps.Add(step);
        CurrentStepId = step.Id;
        return step;
    }

    public AgentTaskStep? CurrentStep => Steps.FirstOrDefault(s => s.Id == CurrentStepId);

    public void AddObservation(Observation o)
    {
        Observations.Add(o);
        if (o.EvidenceRef is not null && CurrentStep is { } step)
            step.ObservationRef = o.EvidenceRef;
    }

    public void AddEvidence(Evidence e)
    {
        // 按内容去重（跨多次执行/重规划时 Index 会重新编号，不能作为去重键）。
        var key = $"{e.SessionId}|{e.CreateTime}|{e.Content}";
        if (Evidence.All(x => $"{x.SessionId}|{x.CreateTime}|{x.Content}" != key))
            Evidence.Add(e);
    }
}
