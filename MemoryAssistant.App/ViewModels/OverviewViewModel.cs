using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoryAssistant.App.Display;
using MemoryAssistant.Core.Missions;
using MemoryAssistant.Core.RAG;
using MemoryAssistant.Infrastructure;

namespace MemoryAssistant.App.ViewModels;

/// <summary>
/// 首页 ViewModel（第三阶段 §9 重做）。
///
/// 目标只有一个：**打开软件就知道这个 Agent 能干什么、刚才干了什么**。
/// 所以这一页不摆概念卡片，只摆三样东西：
///   1) 它现在什么状态（就绪 + 真实规模数字）；
///   2) 最近动态（来自真实工具调用台账 + 后台任务执行记录）；
///   3) "你可以让我……"（点一下就直接问，不用自己组织语言）。
/// 所有数字都读真实组件，读不到就是 "—"，不编造。
/// </summary>
public partial class OverviewViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MissionStore _missions;
    private readonly ChatViewModel _chat;
    private readonly ToolCallLog _toolLog;
    private readonly MissionScheduler _scheduler;

    public OverviewViewModel(
        AppServices services,
        MissionStore missions,
        ChatViewModel chat,
        ToolCallLog toolLog,
        MissionScheduler scheduler)
    {
        _services = services;
        _missions = missions;
        _chat = chat;
        _toolLog = toolLog;
        _scheduler = scheduler;
        RefreshLocalStats();
        RefreshActivities();
    }

    // ---- 身份与状态 ----

    public string AgentName => "Memory Assistant";

    public string Tagline => "你的个人智能体：记得住你说过的话，也能自己去把事情办掉。";

    /// <summary>Agent 已装配 = 能自主规划与调用工具（Bridge 初始化失败时为 false）。</summary>
    public bool AgentReady => _services.Agent is not null;

    public string AgentStatusText => AgentReady
        ? "正常运行"
        : "未就绪（Bridge 初始化中或失败）";

    /// <summary>状态点颜色状态（配 StepStateToBrush：ok / running / failed）。</summary>
    public string AgentStatusState => AgentReady ? "ok" : "failed";

    // ---- 规模数字（真实组件读数） ----

    [ObservableProperty]
    private string _toolCountText = "—";

    [ObservableProperty]
    private string _memoryCountText = "—";

    [ObservableProperty]
    private string _sessionCountText = "—";

    [ObservableProperty]
    private string _missionText = "—";

    [ObservableProperty]
    private string _indexNote = "正在读取本地知识库状态…";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>最近动态（工具调用 + 任务执行，时间倒序）。</summary>
    public ObservableCollection<ActivityItem> Activities { get; } = [];

    public bool HasActivities => Activities.Count > 0;

    /// <summary>"你可以让我……"：点一下直接发问。</summary>
    public IReadOnlyList<string> Suggestions { get; } =
    [
        "最近有人和我聊过秋招吗？",
        "我还有什么答应过的事没做完？",
        "总结一下我最近都在聊什么",
        "帮我每 5 分钟检查一次新的秋招消息",
    ];

    /// <summary>本机真实统计（不依赖 Bridge，先显示出来，避免首页一进来是空的）。</summary>
    private void RefreshLocalStats()
    {
        var tools = _services.Tools?.All.Count ?? 0;
        ToolCountText = tools > 0 ? tools.ToString() : "—";

        var total = _missions.Items.Count;
        var running = _missions.Items.Count(m => m.Status == MissionStatus.Running);
        MissionText = total > 0 ? $"{total}" : "0";
        MissionNote = total > 0 ? $"其中 {running} 个在后台运行" : "还没有长期任务";
    }

    [ObservableProperty]
    private string _missionNote = "";

    private void RefreshActivities()
    {
        Activities.Clear();
        foreach (var a in ActivityFeed.Build(_toolLog, _scheduler)) Activities.Add(a);
        if (Activities.Count == 0)
            Activities.Add(new ActivityItem("—", "提示", "还没有动态", "去「对话」页问一句，或让任务在后台跑一轮，这里会显示它做过什么。"));
        OnPropertyChanged(nameof(HasActivities));
    }

    /// <summary>读取知识库规模 + 刷新动态。</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        RefreshLocalStats();
        RefreshActivities();
        try
        {
            var resp = await _services.Bridge.RequestAsync("rag_index_status", null, timeoutSeconds: 60);
            if (!resp.Success || resp.Data is not System.Text.Json.JsonElement je)
            {
                IndexNote = $"知识库状态读取失败：{resp.Error ?? "无数据"}（不影响对话）";
                return;
            }
            var s = IndexStatusParser.Parse(je);
            if (s.Error is not null)
            {
                IndexNote = $"知识库不可用：{s.Error}";
                return;
            }
            MemoryCountText = s.Chunks.ToString();
            SessionCountText = s.Sessions.ToString();
            IndexNote = s.Loaded
                ? $"{s.TotalMsgs:N0} 条聊天消息已建成知识库" + (s.BuiltAt is null ? "" : $" · 更新于 {s.BuiltAt}")
                : "知识库未加载（可在「知识库」页重建）";
        }
        catch (Exception ex)
        {
            IndexNote = $"知识库状态读取异常：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>点建议 → 直接把这句话发给 Agent（省去用户组织语言）。</summary>
    [RelayCommand]
    private async Task UseSuggestionAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || _chat.IsBusy) return;
        _chat.InputText = text;
        await _chat.SendCommand.ExecuteAsync(null);
    }
}
