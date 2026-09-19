using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Planner;
using MemoryAssistant.Core.Environment;
using MemoryAssistant.Core.Missions;
using MemoryAssistant.Core.Workflow;
using MemoryAssistant.Infrastructure;

namespace MemoryAssistant.App.ViewModels;

/// <summary>能力模型里的一行（Agent 与 GUI 共用同一份目录）。</summary>
public sealed record CapabilityRow(string Name, string Count, string Detail);

/// <summary>
/// 「环境」页 ViewModel（第三阶段 §29）：Agent 环境感知的可视化入口。
///
/// 这一页回答的是"这个 Agent 知道自己什么"：它跑在哪个软件里、当前在哪个页面、
/// 有能力多少项、任务几个在跑、知识库/微信/调度器/模型是否正常。
/// 数据全部来自 Infrastructure 的 EnvironmentService —— 和 Agent 调 get_environment_context
/// 拿到的是同一份快照（规范 §41：Agent 看到的软件世界和用户看到的应该是同一个）。
/// </summary>
public partial class EnvironmentViewModel : ObservableObject
{
    private readonly AppServices _services;

    public EnvironmentViewModel(AppServices services)
    {
        _services = services;
        Refresh();
    }

    // ---- 身份与状态 ----

    [ObservableProperty]
    private string _agentName = "Memory Assistant";

    [ObservableProperty]
    private string _version = "";

    [ObservableProperty]
    private string _agentStatus = "";

    /// <summary>状态点键（ok / warning / failed）。</summary>
    [ObservableProperty]
    private string _agentState = "pending";

    [ObservableProperty]
    private string _currentPage = "";

    [ObservableProperty]
    private string _llmModel = "";

    [ObservableProperty]
    private string _knowledgeStatus = "";

    [ObservableProperty]
    private string _weChatStatus = "";

    [ObservableProperty]
    private string _schedulerStatus = "";

    /// <summary>能力模型（Tools / Skills / Workflows / Missions / Actions / Environment）。</summary>
    public ObservableCollection<CapabilityRow> Capabilities { get; } = [];

    /// <summary>写操作类工具（必须人工确认的那些）。</summary>
    public ObservableCollection<ToolRow> WriteTools { get; } = [];

    // ---- 自检 ----

    public ObservableCollection<SelfCheckItem> Checks { get; } = [];

    [ObservableProperty]
    private string _checkSummary = "还没跑过自检。点「运行自检」会逐项去问 LLM、知识库、微信、工具表和调度器。";

    [ObservableProperty]
    private bool _isChecking;

    [RelayCommand]
    private async Task RunSelfCheckAsync()
    {
        if (IsChecking) return;
        var env = _services.Environment;
        if (env is null)
        {
            CheckSummary = "环境层未装配（服务未初始化）。";
            return;
        }

        IsChecking = true;
        Checks.Clear();
        CheckSummary = "自检中…（微信这一项会真的去列一次窗口，稍等）";
        try
        {
            var report = await env.SelfCheckAsync();
            foreach (var item in report.Items) Checks.Add(item);
            CheckSummary = report.AllOk
                ? $"全部正常：{report.Items.Count} 项检查全通过。"
                : $"{report.OkCount}/{report.Items.Count} 项正常，下面标红的是有问题的项。";
        }
        catch (Exception ex)
        {
            CheckSummary = $"自检异常：{ex.Message}";
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>重新读一次环境（页面加载/手动刷新时调用）。</summary>
    [RelayCommand]
    private void Refresh()
    {
        var env = _services.Environment;
        if (env is null)
        {
            AgentStatus = "环境层未装配（服务未初始化）";
            AgentState = "failed";
            return;
        }

        var s = env.Capture();
        AgentName = s.Application;
        Version = s.Version;
        AgentStatus = s.AgentStatus;
        AgentState = _services.Agent is not null ? "ok" : "failed";
        CurrentPage = s.CurrentPage;
        LlmModel = s.LlmModel;
        KnowledgeStatus = s.KnowledgeStatus;
        WeChatStatus = s.WeChatStatus;
        SchedulerStatus = s.SchedulerStatus;

        Capabilities.Clear();
        Capabilities.Add(new CapabilityRow("Tools", $"{s.Tools.Count}", "可调用的工具（含外部清单热注册）"));
        Capabilities.Add(new CapabilityRow("Skills", $"{s.Skills.Count}", "内置能力：回忆 / 统计 / 时间线 / 承诺 / 话题 / 画像…"));
        Capabilities.Add(new CapabilityRow("Workflows", $"{s.Workflows.Count}", $"固定阶段 {s.WorkflowStages.Count} 步：{string.Join(" → ", s.WorkflowStages)}"));
        Capabilities.Add(new CapabilityRow("Missions", $"{s.MissionCount}", $"长期任务（运行中 {s.RunningMissionCount} 个）"));
        Capabilities.Add(new CapabilityRow("Actions", $"{_services.Tools?.All.Count(t => !t.ReadOnly) ?? 0}", "会改状态的动作：全部要人工确认（见下表）"));
        Capabilities.Add(new CapabilityRow("Environment", s.CurrentPage, $"我能感知到的运行时上下文：当前页面={s.CurrentPage}"));

        WriteTools.Clear();
        var tools = _services.Tools?.All;
        if (tools is not null)
        {
            foreach (var t in tools.Where(t => !t.ReadOnly).OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                WriteTools.Add(new ToolRow(
                    t.Name,
                    t.Description,
                    t.Category,
                    t.ReadOnly,
                    "写操作 · 需人工确认",
                    t.Parameters.Count == 0 ? "无参数" : $"{t.Parameters.Count} 个参数",
                    []));
            }
        }

        OnPropertyChanged(nameof(HasWriteTools));
    }

    public bool HasWriteTools => WriteTools.Count > 0;

    /// <summary>风险分级说明（§25）：给老师看的安全设计。</summary>
    public IReadOnlyList<CapabilityRow> RiskRules { get; } =
    [
        new("读（list / get / 查询）", "低风险", "直接执行，不打扰用户"),
        new("创建 / 修改（create / update）", "中风险", "先把「将要做成什么」摆出来，用户点确认才执行"),
        new("删除（delete）", "高风险", "明确确认，且不可恢复"),
        new("对外动作（发消息 / 自动回复）", "高风险", "必须人工确认；演示默认不向真人发送"),
    ];
}
