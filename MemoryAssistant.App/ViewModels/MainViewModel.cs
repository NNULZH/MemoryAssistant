using CommunityToolkit.Mvvm.ComponentModel;
using MemoryAssistant.App.Display;
using MemoryAssistant.Core.Missions;
using MemoryAssistant.Infrastructure;
using MemoryAssistant.Infrastructure.Missions;

namespace MemoryAssistant.App.ViewModels;

/// <summary>主窗口 ViewModel：持有子页面 VM 与全局服务。</summary>
public partial class MainViewModel : ObservableObject
{
    public MainViewModel(AppServices services)
    {
        Services = services;

        // 任务子系统：目录/仓库/调度器统一由 AppServices 装配（对话编排出的任务与任务页共用同一份）
        MissionRepository = services.MissionRepository
            ?? throw new InvalidOperationException("任务仓库未装配（AppServices 未初始化）。");
        MissionCatalog = services.MissionCatalog
            ?? throw new InvalidOperationException("任务目录未装配（AppServices 未初始化）。");
        Scheduler = services.Scheduler
            ?? throw new InvalidOperationException("任务调度器未装配（缺少任务执行器）。");
        MissionComposer = services.MissionComposer
            ?? throw new InvalidOperationException("任务编排写侧未装配。");

        // 首次运行：放入两个示例任务（说明"需求可以被固定下来"），之后完全以本地文件为准
        if (MissionCatalog.Items.Count == 0)
        {
            foreach (var sample in MissionStore.WithSamples().Items) MissionCatalog.Add(sample);
            MissionRepository.Save(MissionCatalog.Items);
        }

        Scheduler.Start();

        // 课程展示版：工具调用台账由 MainViewModel 持有，对话页写入、工具页读取（同一份，不各存一套）
        ToolCalls = new ToolCallLog();

        Chat = new ChatViewModel(services, ToolCalls);
        Search = new SearchViewModel(services);
        Timeline = new TimelineViewModel(services);
        Commitments = new CommitmentsViewModel(services);
        Topics = new TopicsViewModel(services);
        Profiles = new ProfilesViewModel(services);
        Settings = new SettingsViewModel(services);

        Overview = new OverviewViewModel(services, MissionCatalog, Chat, ToolCalls, Scheduler);
        Knowledge = new KnowledgeViewModel(services);
        Tools = new ToolsViewModel(services, ToolCalls);
        Workflow = new WorkflowViewModel(Chat);
        Environment = new EnvironmentViewModel(services);

        Missions = new MissionsViewModel(MissionCatalog, Scheduler,
            () => MissionRepository.Save(MissionCatalog.Items), services.MissionRuns);

        // 对话里确认创建的任务 → 任务页立即同步（同一份目录，不再"两套任务"）
        Chat.MissionCreated += _ => Missions.SyncFromStore();
    }

    public AppServices Services { get; }
    public ChatViewModel Chat { get; }
    public SearchViewModel Search { get; }
    public TimelineViewModel Timeline { get; }
    public CommitmentsViewModel Commitments { get; }
    public TopicsViewModel Topics { get; }
    public ProfilesViewModel Profiles { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>工具调用台账（对话页写入 / 工具页展示）。</summary>
    public ToolCallLog ToolCalls { get; }

    // ---- 课程展示版新增页面（让既有 Agent 能力"被看见"） ----
    public OverviewViewModel Overview { get; }
    public KnowledgeViewModel Knowledge { get; }
    public ToolsViewModel Tools { get; }
    public WorkflowViewModel Workflow { get; }
    public EnvironmentViewModel Environment { get; }

    public JsonMissionRepository MissionRepository { get; }
    public MissionStore MissionCatalog { get; }
    public MissionScheduler Scheduler { get; }
    public IMissionComposer MissionComposer { get; }
    public MissionsViewModel Missions { get; }

    [ObservableProperty]
    private string _pageTitle = "首页";

    /// <summary>顶部状态条一行摘要（Agent 是否就绪 + 工具数 + 任务规模），由窗口在切页时刷新。</summary>
    [ObservableProperty]
    private string _shellStatus = "正在初始化…";

    /// <summary>顶部状态条的状态点（ok / running / failed）。</summary>
    [ObservableProperty]
    private string _shellState = "pending";

    /// <summary>
    /// 刷新状态条。只读本机内存里的真实计数（不请求 Bridge），所以切页时调用也够快。
    /// </summary>
    public void RefreshShellStatus()
    {
        var agentReady = Services.Agent is not null;
        var tools = Services.Tools?.All.Count ?? 0;
        var missions = MissionCatalog.Items.Count;
        var running = MissionCatalog.Items.Count(m => m.Status == MissionStatus.Running);

        ShellState = agentReady ? "ok" : "failed";
        ShellStatus = agentReady
            ? $"就绪 · {tools} 个工具 · {missions} 个任务（{running} 运行中）"
            : "未就绪（Bridge 初始化中或失败）";
    }
}
