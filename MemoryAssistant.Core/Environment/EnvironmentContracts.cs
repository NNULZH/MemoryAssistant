namespace MemoryAssistant.Core.Environment;

/// <summary>
/// 界面当前状态（由 WPF 层发布，Agent 通过 get_ui_context / get_environment_context 读取）。
///
/// 为什么要这个：用户说"把刚才那个任务暂停"时，Agent 必须先知道"刚才那个"指的是哪个——
/// 这个信息只存在于界面里。Core 只定义契约，界面层负责填。
/// </summary>
public interface IUiContext
{
    /// <summary>当前页面名（首页 / 对话 / 任务 / 知识库 / 工具 / 工作流 / 环境 / 设置…）。</summary>
    string CurrentPage { get; }
    /// <summary>当前选中的会话（聊天记录窗口/知识库里的对象），无则空。</summary>
    string SelectedSession { get; }
    /// <summary>当前选中的任务名，无则空。</summary>
    string SelectedMission { get; }
    /// <summary>当前打开的对话框（确认弹窗 / 聊天记录窗口），无则空。</summary>
    string OpenDialog { get; }
    /// <summary>正在等待用户确认的动作描述，无则空。</summary>
    string PendingConfirmation { get; }
}

/// <summary>无界面（自检/命令行模式）下的空实现：如实回答"未知"，不编造。</summary>
public sealed class NullUiContext : IUiContext
{
    public string CurrentPage => "无界面（命令行/自检模式）";
    public string SelectedSession => "";
    public string SelectedMission => "";
    public string OpenDialog => "";
    public string PendingConfirmation => "";
}

/// <summary>Agent 对"自己身处什么软件"的一次快照（环境感知的返回值）。</summary>
public sealed record EnvironmentSnapshot
{
    public string Application { get; init; } = "Memory Assistant";
    public string Version { get; init; } = "";
    public string Role { get; init; } = "个人聊天记忆智能体";

    // ---- 运行时上下文（来自界面） ----
    public string CurrentPage { get; init; } = "";
    public string SelectedSession { get; init; } = "";
    public string SelectedMission { get; init; } = "";
    public string OpenDialog { get; init; } = "";
    public string PendingConfirmation { get; init; } = "";

    // ---- 能力（与 GUI 共用同一份目录） ----
    public IReadOnlyList<string> Tools { get; init; } = [];
    public IReadOnlyList<string> Skills { get; init; } = [];
    public IReadOnlyList<string> Workflows { get; init; } = [];
    public IReadOnlyList<string> WorkflowStages { get; init; } = [];

    // ---- 任务 ----
    public int MissionCount { get; init; }
    public int RunningMissionCount { get; init; }
    public IReadOnlyList<string> RunningMissionTitles { get; init; } = [];

    // ---- 子系统状态 ----
    public string LlmModel { get; init; } = "";
    public string KnowledgeStatus { get; init; } = "";
    public string WeChatStatus { get; init; } = "";
    public string SchedulerStatus { get; init; } = "";
    public string AgentStatus { get; init; } = "";
}

/// <summary>自检里的一项。</summary>
public sealed record SelfCheckItem(string Name, bool Ok, string Detail);

/// <summary>自检报告（self_check 工具的返回值，也是"环境"页的一屏结论）。</summary>
public sealed record SelfCheckReport(IReadOnlyList<SelfCheckItem> Items)
{
    public bool AllOk => Items.All(i => i.Ok);
    public int OkCount => Items.Count(i => i.Ok);
}
