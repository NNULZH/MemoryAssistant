using MemoryAssistant.Core.Agent.Planner;
using MemoryAssistant.Core.Agent.Skills;

namespace MemoryAssistant.Core.Agent.Runtime;

/// <summary>任务状态变更事件参数（供 UI 任务栏 / Trace / 日志观察）。</summary>
public sealed class AgentTaskStatusChangedEventArgs(AgentTaskStatus status, string message) : EventArgs
{
    public AgentTaskStatus Status { get; } = status;
    public string Message { get; } = message;
}

/// <summary>
/// 一个可观察、可取消的 Agent 任务（plan2 §3.1 / P11-C）。
/// 持有任务状态（TaskState）与最终结果（AgentResult，复用 P2 的轨迹/回答模型）。
/// </summary>
public sealed class AgentTask
{
    private readonly CancellationTokenSource _cts = new();
    private AgentTaskStatus _status = AgentTaskStatus.Created;

    public AgentTask(string userQuery)
    {
        UserQuery = userQuery;
        State = new TaskState(userQuery);
        State.Goal = userQuery;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string UserQuery { get; }
    /// <summary>本轮的意图提示（来自会话层 FollowUpResolver 或上层；Agent 可忽略，plan2 §1.3）。</summary>
    public PlannerHint? Hint { get; set; }
    /// <summary>V3.3：本轮检索时间窗（由 QueryModel 解析），下传给 Skill 做候选/精读过滤。</summary>
    public SearchWindow? Window { get; set; }
    /// <summary>
    /// 会话上文（最近几轮问答）：作答时要接着上文说话，而不是每轮都当陌生人。
    /// 由会话层（ConversationalAgent）填充；单轮/自动化调用留空即可。
    /// </summary>
    public IReadOnlyList<Answer.AnswerTurn> History { get; set; } = [];
    /// <summary>上一轮谈到的人（会话记忆），用于自然衔接（"他""她"）。</summary>
    public string? LastPerson { get; set; }
    /// <summary>上一轮的焦点短语（"还有吗/再早一点"续查用）。</summary>
    public string? Focus { get; set; }
    public TaskState State { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? StartedAt { get; internal set; }
    public DateTimeOffset? FinishedAt { get; internal set; }

    public AgentTaskStatus Status => _status;
    public string? StatusMessage { get; private set; }
    public AgentResult? Result { get; internal set; }
    public string? Error { get; internal set; }
    public bool IsCancellationRequested => _cts.IsCancellationRequested;

    public event EventHandler<AgentTaskStatusChangedEventArgs>? StatusChanged;

    public double ElapsedMs => ((FinishedAt ?? DateTimeOffset.UtcNow) - CreatedAt).TotalMilliseconds;

    /// <summary>请求取消；向下传播给 TaskRuntime 与执行中的工具（plan2 §20.4）。</summary>
    public void Cancel()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    public CancellationToken Token => _cts.Token;

    internal void Transition(AgentTaskStatus status, string message)
    {
        if (_status == status) return;
        _status = status;
        StatusMessage = message;
        switch (status)
        {
            case AgentTaskStatus.Executing:
                StartedAt ??= DateTimeOffset.UtcNow;
                break;
            case AgentTaskStatus.Completed:
            case AgentTaskStatus.Failed:
            case AgentTaskStatus.Cancelled:
                FinishedAt ??= DateTimeOffset.UtcNow;
                break;
        }
        StatusChanged?.Invoke(this, new AgentTaskStatusChangedEventArgs(status, message));
    }
}
