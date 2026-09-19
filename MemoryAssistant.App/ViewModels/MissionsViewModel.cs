using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoryAssistant.Core.Missions;

namespace MemoryAssistant.App.ViewModels;

/// <summary>触发方式下拉项（V3.6）。</summary>
public sealed record TriggerOption(MissionTriggerKind Kind, string Label);

/// <summary>
/// 任务页 ViewModel（V3.2）：任务列表 + 启停/立即执行 + 运行状态（下次运行/上次结果）+ 日志。
/// 调度器真实后台巡检，状态变化经 Dispatcher 刷新到 UI。
/// </summary>
public partial class MissionsViewModel : ObservableObject
{
    private readonly MissionStore _store;
    private readonly MissionScheduler _scheduler;
    private readonly Action _persist;
    private readonly IMissionRunStore? _runs;

    public MissionsViewModel(MissionStore store, MissionScheduler scheduler, Action persist, IMissionRunStore? runStore = null)
    {
        _store = store;
        _scheduler = scheduler;
        _persist = persist;
        _runs = runStore;

        Missions = new ObservableCollection<MissionDefinition>(store.Items);
        SelectedMission = Missions.FirstOrDefault();
        _scheduler.Changed += OnSchedulerChanged;
        RefreshLog();
    }

    public ObservableCollection<MissionDefinition> Missions { get; }
    public ObservableCollection<string> Logs { get; } = [];
    public string CapabilityNote => MissionScheduler.CapabilityNote;

    /// <summary>可选的触发方式（V3.6：让任务真正"可编排"）。</summary>
    public IReadOnlyList<TriggerOption> TriggerOptions { get; } =
    [
        new(MissionTriggerKind.Manual, "手动执行"),
        new(MissionTriggerKind.Interval, "定时执行"),
        new(MissionTriggerKind.Watch, "追踪会话（有新消息才执行）"),
    ];

    [ObservableProperty]
    private MissionDefinition? _selectedMission;

    [ObservableProperty]
    private string _newTitle = "";

    [ObservableProperty]
    private string _newGoal = "";

    [ObservableProperty]
    private string _newTarget = "";

    [ObservableProperty]
    private MissionTriggerKind _newTrigger = MissionTriggerKind.Manual;

    [ObservableProperty]
    private string _newIntervalText = "30";

    /// <summary>新任务是否走"自动回复"（写操作：创建后会自动发消息，运行期不再逐条确认）。</summary>
    [ObservableProperty]
    private bool _newAutoReply;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>任务规模（列表上方一行："共 N 个任务 · 运行中 M 个"）。</summary>
    [ObservableProperty]
    private string _missionStatsText = "";

    public bool HasSelection => SelectedMission is not null;

    /// <summary>选中任务的状态键（配 StepStateToBrush 给状态点上色）。</summary>
    public string StatusState => SelectedMission?.StatusState ?? "pending";

    /// <summary>选中任务的上次执行时间 / 执行次数（详情面板用）。</summary>
    public string LastRunAtText => SelectedMission?.LastRunAtText ?? "—";
    public string RunCountText => SelectedMission?.RunCountText ?? "—";

    /// <summary>
    /// 创建方式（§23）：Agent 创建 / 手工创建。
    /// 这是很有展示价值的一栏——它说明"这个任务不是用户手填的配置，而是 Agent 从自然语言里理解出来的"。
    /// </summary>
    public string OriginText => SelectedMission?.OriginText ?? "—";

    public bool IsAgentCreated => SelectedMission?.Origin == MissionDefinition.OriginAgent;

    /// <summary>
    /// 执行流程（§23）：按选中任务的真实触发方式/动作描述它一轮会怎么走。
    /// 不是示意图——每一句都对应调度器/执行器里真实存在的步骤。
    /// </summary>
    public string FlowText
    {
        get
        {
            if (SelectedMission is not { } m) return "—";
            var steps = new List<string>();
            steps.Add(m.Trigger switch
            {
                MissionTriggerKind.Watch => "① 到点探测：先看追踪对象有没有新内容（没有就跳过，不空转）",
                MissionTriggerKind.Interval => "① 到点触发",
                _ => "① 手动触发",
            });
            if (m.Action == MissionActionKind.AutoReply)
            {
                steps.Add("② 抓取对方发来的新消息");
                steps.Add("③ 子智能体生成回复（写操作 → 人工确认闸门）");
                steps.Add("④ 发送并只回关键信息到任务日志");
            }
            else
            {
                steps.Add("② 交给独立 Agent 执行任务目标（查记录 / 检索记忆 / 调用工具）");
                steps.Add("③ 汇总成结论并写入执行结果");
            }
            steps.Add($"④ 记录本次执行（第 {m.RunCount + 1} 次）与轨迹");
            return string.Join("\n", steps);
        }
    }

    /// <summary>
    /// 执行记录（第三阶段补充）：来自**持久化账本**（跨重启保留、正文不截断），
    /// 不再是从内存日志里抠字符串。这正是用户提的三件事：
    /// 可查看（点一条看全文 + 轨迹）、可删除（单条 / 清空该任务）、长期存下来。
    /// </summary>
    public ObservableCollection<MissionRunRecord> Runs { get; } = [];

    /// <summary>当前选中的那一条记录（右侧显示它的完整结果）。</summary>
    [ObservableProperty]
    private MissionRunRecord? _selectedRun;

    public string RunNote => _runs is null
        ? "未接入执行账本（本次会话内不保留执行记录）。"
        : Runs.Count == 0
            ? "这个任务还没有执行记录（跑一次就会出现；记录会长期保存）。"
            : $"执行记录（长期保存，共 {Runs.Count} 条）：点一条看完整结果。";

    /// <summary>账本落盘位置（告诉用户"记在哪"）。</summary>
    public string RunLocationNote => _runs is { } s && s.Location.Length > 0
        ? $"记录存放在：{s.Location}"
        : "";

    /// <summary>追踪型、或勾了自动回复，都需要填"对象"（自动回复说不清回给谁就不该建）。</summary>
    public bool NeedsTarget => NewTrigger == MissionTriggerKind.Watch || NewAutoReply;

    /// <summary>定时/追踪都需要间隔（手动执行不需要）。</summary>
    public bool NeedsInterval => NewTrigger != MissionTriggerKind.Manual;

    partial void OnNewAutoReplyChanged(bool value) => OnPropertyChanged(nameof(NeedsTarget));

    partial void OnNewTriggerChanged(MissionTriggerKind value)
    {
        OnPropertyChanged(nameof(NeedsTarget));
        OnPropertyChanged(nameof(NeedsInterval));
    }

    /// <summary>手动任务不参与自动调度（只能"立即执行一次"）。</summary>
    public bool IsSchedulable => SelectedMission is { } m && m.Trigger != MissionTriggerKind.Manual;

    /// <summary>
    /// 当前是否有一轮执行正在进行（选中任务的子智能体正在跑）。
    /// 「终止执行」按钮据此点亮——以前把"停止任务"和"终止这一轮"混成一个按钮，
    /// 而且只对定时/追踪任务可用，手动任务跑起来后根本掐不断。
    /// </summary>
    public bool IsExecutingNow => SelectedMission is { } m && _scheduler.IsExecuting(m.Id);

    /// <summary>状态行右侧的一行提示（正在执行时显示）。</summary>
    public string ExecutionStateText => IsExecutingNow ? "正在执行…（可点「终止执行」掐断）" : "";

    /// <summary>终止选中任务**正在进行的那一轮**（任务本身不停用，下个周期照常跑）。</summary>
    [RelayCommand]
    private void CancelRun()
    {
        if (SelectedMission is not { } m) return;
        var ok = _scheduler.CancelRun(m.Id);
        RefreshLog(ok
            ? $"已终止「{m.Title}」正在进行的执行"
            : $"「{m.Title}」当前没有正在进行的执行");
        RefreshAll();
    }

    public string NextRunText
    {
        get
        {
            if (SelectedMission is null) return "—";
            var next = _scheduler.NextRun(SelectedMission.Id);
            if (next is null) return "未调度";
            var delta = next.Value - DateTimeOffset.Now;
            if (delta.TotalSeconds <= 0) return "即将执行";
            // 秒级任务（如每 30 秒）显示"秒后"，否则永远显示成"1 分钟后"，看着像坏了
            var shown = delta.TotalMinutes >= 1
                ? $"{Math.Ceiling(delta.TotalMinutes)} 分钟后"
                : $"{Math.Ceiling(delta.TotalSeconds)} 秒后";
            return $"{next.Value:HH:mm:ss}（{shown}）";
        }
    }

    public string LastResultText => SelectedMission?.LastResult ?? "尚无执行记录";

    partial void OnSelectedMissionChanged(MissionDefinition? value)
    {
        // 告诉环境感知层"用户现在选中的是哪个任务"——用户说"把刚才那个任务暂停"时靠它定位
        UiContext.Current.SetMission(value?.Title);
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsSchedulable));
        OnPropertyChanged(nameof(NextRunText));
        OnPropertyChanged(nameof(LastResultText));
        OnPropertyChanged(nameof(StatusState));
        OnPropertyChanged(nameof(LastRunAtText));
        OnPropertyChanged(nameof(RunCountText));
        OnPropertyChanged(nameof(OriginText));
        OnPropertyChanged(nameof(IsAgentCreated));
        OnPropertyChanged(nameof(FlowText));
        OnPropertyChanged(nameof(IsExecutingNow));
        OnPropertyChanged(nameof(ExecutionStateText));
        RefreshRuns();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    /// <summary>从持久化账本里读这个任务的执行记录（倒序，最新的在上）。</summary>
    private void RefreshRuns()
    {
        var keep = SelectedRun?.Id;
        if (Runs.Count > 0) Runs.Clear();
        if (SelectedMission is { } m && _runs is not null)
            foreach (var r in _runs.List(m.Id, limit: 100)) Runs.Add(r);
        SelectedRun = keep is null ? null : Runs.FirstOrDefault(r => r.Id == keep);
        OnPropertyChanged(nameof(RunNote));
        OnPropertyChanged(nameof(RunLocationNote));
    }

    private void OnSchedulerChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) { RefreshAll(); return; }
        dispatcher.Invoke(RefreshAll);
    }

    [RelayCommand]
    private void Start()
    {
        if (SelectedMission is null) return;
        _scheduler.StartMission(SelectedMission.Id);
        _persist();
        RefreshAll();
    }

    [RelayCommand]
    private void Stop()
    {
        if (SelectedMission is null) return;
        _scheduler.StopMission(SelectedMission.Id);
        _persist();
        RefreshAll();
    }

    /// <summary>立即执行一次（复用调度器的执行器，不等间隔）。</summary>
    [RelayCommand]
    private async Task RunNowAsync()
    {
        if (SelectedMission is null || IsBusy) return;
        IsBusy = true;
        try
        {
            var r = await _scheduler.RunOnceAsync(SelectedMission.Id);
            if (r is not null && r.TraceText is not null) LastTrace = r.TraceText;
            _persist();
            RefreshAll();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [ObservableProperty]
    private string _lastTrace = "";

    /// <summary>
    /// 解析间隔输入框：<c>"30"</c> = 30 分钟；<c>"30秒" / "30s"</c> = 秒级（V4.1）；
    /// 解析不出来回退 30 分钟（不阻塞创建）。
    /// </summary>
    internal static (int Minutes, int Seconds) ParseInterval(string? raw)
    {
        var t = (raw ?? "").Trim();
        var m = System.Text.RegularExpressions.Regex.Match(t, @"^(\d{1,4})\s*(?:秒|s|S)$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var sec) && sec > 0) return (0, sec);
        return int.TryParse(t, out var min) && min > 0 ? (min, 0) : (30, 0);
    }

    [RelayCommand]
    private void CreateMission()
    {
        if (string.IsNullOrWhiteSpace(NewTitle) && string.IsNullOrWhiteSpace(NewGoal)) return;
        var title = NewTitle.Trim();
        var goal = NewGoal.Trim();
        var target = NewTarget.Trim();
        var trigger = NewTrigger;
        var autoReply = NewAutoReply;
        // 自动回复是写操作：说不清回给谁就不建（不猜一个对象出来）
        if (autoReply && target.Length == 0)
        {
            RefreshLog("自动回复任务需要填「回复对象」，已取消创建");
            return;
        }
        // 间隔解析失败回退 30 分钟（不阻塞创建）；支持 "30"（分钟）与 "30秒"/"30s"（秒级）
        var (interval, intervalSeconds) = ParseInterval(NewIntervalText);
        var m = _store.Add(new MissionDefinition
        {
            Title = string.IsNullOrWhiteSpace(title)
                ? goal[..Math.Min(goal.Length, 16)]
                : title,
            Goal = string.IsNullOrWhiteSpace(goal) ? title : goal,
            Trigger = trigger,
            Action = autoReply ? MissionActionKind.AutoReply : MissionActionKind.Summarize,
            Target = autoReply || trigger == MissionTriggerKind.Watch ? target : "",
            IntervalMinutes = interval,
            IntervalSeconds = intervalSeconds,
            RequiresApproval = true,
            // 这一条是用户在任务页手填的（Agent 创建的那条路径走 MissionComposer，会标 agent）
            Origin = MissionDefinition.OriginUser,
        });
        Missions.Add(m);
        SelectedMission = m;
        NewTitle = "";
        NewGoal = "";
        NewTarget = "";
        NewAutoReply = false;
        _persist();
        RefreshLog($"新任务已创建（{m.TriggerText}）{(autoReply ? "· 会自动发送，请确认对象无误" : "")}");
    }

    /// <summary>
    /// 删除**单条**执行记录（第三阶段补充：任务记忆可删除）。
    /// 只动账本，不动任务本身——用户只是不想留这一条。
    /// </summary>
    [RelayCommand]
    private void DeleteRun()
    {
        if (SelectedRun is not { } r || _runs is null) return;
        _runs.Delete(r.Id);
        SelectedRun = null;
        RefreshRuns();
        RefreshLog($"已删除一条执行记录（{r.AtText} {r.StatusText}）");
    }

    /// <summary>清空当前任务的全部执行记录（清记录不影响任务运行）。</summary>
    [RelayCommand]
    private void ClearRuns()
    {
        if (SelectedMission is not { } m || _runs is null) return;
        var n = _runs.Clear(m.Id);
        SelectedRun = null;
        RefreshRuns();
        RefreshLog($"已清空「{m.Title}」的 {n} 条执行记录");
    }

    /// <summary>
    /// 删除任务本身（第三阶段补充）：不可逆，所以先让用户看清"删哪个、连记录一起删"。
    /// 删除时把它的**执行记录**一并清掉——记录属于这个任务，留着只会变成读不懂的孤儿数据。
    /// </summary>
    [RelayCommand]
    private void DeleteMission()
    {
        if (SelectedMission is not { } m) return;
        var extra = _runs?.List(m.Id, limit: 1).Count > 0 ? "它的执行记录也会一并删除，" : "";
        var ok = MessageBox.Show(
            $"删除任务「{m.Title}」？\n\n{extra}删除后无法恢复（此操作不可撤销）。",
            "删除任务", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (!ok) return;

        var title = m.Title;
        _scheduler.RemoveMission(m.Id);
        Missions.Remove(m);
        SelectedMission = Missions.FirstOrDefault();
        _persist();
        RefreshAll();
        RefreshLog($"已删除任务「{title}」（及其执行记录）");
    }

    /// <summary>
    /// 手动刷新任务列表与运行状态（V4.1）。任务页原来只在"切页 / 调度器事件"时刷新，
    /// 而调度事件在秒级任务下会刷得很勤、在长周期任务下又很久不刷——给一个手动入口最省心。
    /// </summary>
    [RelayCommand]
    private void Refresh()
    {
        RefreshAll();
        var running = Missions.Count(m => m.Status == MissionStatus.Running);
        RefreshLog($"已刷新任务列表：共 {Missions.Count} 个，运行中 {running} 个");
    }

    private void RefreshAll()
    {
        SyncFromStore();
        OnPropertyChanged(nameof(NextRunText));
        OnPropertyChanged(nameof(LastResultText));
        OnPropertyChanged(nameof(StatusState));
        OnPropertyChanged(nameof(LastRunAtText));
        OnPropertyChanged(nameof(RunCountText));
        OnPropertyChanged(nameof(OriginText));
        OnPropertyChanged(nameof(IsAgentCreated));
        OnPropertyChanged(nameof(FlowText));
        OnPropertyChanged(nameof(IsExecutingNow));
        OnPropertyChanged(nameof(ExecutionStateText));
        MissionStatsText = $"共 {Missions.Count} 个任务 · 运行中 {Missions.Count(m => m.Status == MissionStatus.Running)} 个";
        RefreshRuns();
        RefreshLog();
    }

    /// <summary>
    /// 把任务目录的当前内容同步到 UI 列表（对话里确认创建的任务会立刻出现在这里）。
    /// </summary>
    public void SyncFromStore()
    {
        var keep = SelectedMission;
        foreach (var m in _store.Items)
            if (!Missions.Contains(m)) Missions.Add(m);
        for (int i = Missions.Count - 1; i >= 0; i--)
            if (!_store.Items.Contains(Missions[i])) Missions.RemoveAt(i);

        var idx = keep is null ? -1 : Missions.IndexOf(keep);
        if (idx < 0 && Missions.Count > 0) idx = 0;
        SelectedMission = idx < 0 ? null : Missions[idx];

        // MissionDefinition 不是可观察对象：状态/上次执行/执行次数是"算出来的"属性，
        // 跑完一轮不会自己通知。重建一次列表让卡片上的这些值重新求值（数量很少，代价可忽略）。
        BumpList();
    }

    private void BumpList()
    {
        if (Missions.Count == 0) return;
        var keep = SelectedMission;
        var snapshot = Missions.ToList();
        Missions.Clear();
        foreach (var m in snapshot) Missions.Add(m);
        if (keep is not null) SelectedMission = keep;
    }

    private void RefreshLog(string? prefix = null)
    {
        Logs.Clear();
        if (prefix is not null) Logs.Add($"[{DateTimeOffset.Now:HH:mm:ss}] {prefix}");
        foreach (var l in _scheduler.Log) Logs.Add(l);
    }
}
