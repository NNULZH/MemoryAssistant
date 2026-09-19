using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoryAssistant.App.Display;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Infrastructure;

namespace MemoryAssistant.App.ViewModels;

/// <summary>工具详情里的一个参数。</summary>
public sealed record ToolParamRow(string Name, string Type, string RequiredText, string Description);

/// <summary>工具清单里的一行（含参数声明与读写属性）。</summary>
public sealed record ToolRow(
    string Name,
    string Description,
    string Category,
    bool ReadOnly,
    string ReadOnlyText,
    string ParamSummary,
    IReadOnlyList<ToolParamRow> Parameters);

/// <summary>
/// 工具页 ViewModel。
///
/// 课程要求"智能体需要包含工具"——这一页就是把 ToolRegistry 的真实内容摆出来：
/// 有哪些工具、各自什么参数、是只读还是写操作（写操作要人工确认），以及最近真实发生过的调用。
/// 清单来自注册表本身（含外部清单热注册进来的工具），调用记录来自对话页的台账，都不是手写的说明。
/// </summary>
public partial class ToolsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ToolCallLog _log;

    public ToolsViewModel(AppServices services, ToolCallLog log)
    {
        _services = services;
        _log = log;
        RecentCalls = log.Recent;
        Refresh();

        // 外部工具清单增删要立刻反映到这一页（热注册的意义就在"实时可见"）
        if (services.ToolHub is { } hub) hub.Changed += _ => OnUi(Refresh);
    }

    /// <summary>工具清单（来自真实注册表）。</summary>
    public ObservableCollection<ToolRow> Tools { get; } = [];

    /// <summary>最近的工具调用（新的在前，来自对话页的台账）。</summary>
    public ObservableCollection<ToolCallRecord> RecentCalls { get; }

    [ObservableProperty]
    private ToolRow? _selectedTool;

    [ObservableProperty]
    private string _countSummary = "";

    [ObservableProperty]
    private string _emptyNote = "";

    /// <summary>选中工具的"最近一次调用"（没有就如实说没有）。</summary>
    public string SelectedLastCallText
    {
        get
        {
            if (SelectedTool is null) return "";
            var hit = RecentCalls.FirstOrDefault(r => r.Name == SelectedTool.Name);
            return hit is null
                ? "本次启动以来还没有调用过这个工具。"
                : $"{hit.TimeText} · {hit.Headline} → {(hit.Success ? "成功" : "失败")} {hit.ElapsedText}";
        }
    }

    public string TotalCallText => _log.TotalCount == 0
        ? "本次启动以来还没有工具调用记录（去「对话」页问一句试试）。"
        : $"本次启动以来共发生 {_log.TotalCount} 次工具调用。";

    partial void OnSelectedToolChanged(ToolRow? value)
    {
        OnPropertyChanged(nameof(SelectedLastCallText));
        OnPropertyChanged(nameof(HasSelection));
    }

    public bool HasSelection => SelectedTool is not null;

    /// <summary>重新读取注册表（外部清单热注册后也走这里）。</summary>
    [RelayCommand]
    private void Refresh()
    {
        var all = _services.Tools?.All;
        if (all is null)
        {
            EmptyNote = "工具注册表未初始化（Bridge 可能未启动）。";
            return;
        }

        var keep = SelectedTool?.Name;
        Tools.Clear();
        foreach (var t in all
                     .OrderByDescending(t => t.ReadOnly)
                     .ThenBy(t => t.Category, StringComparer.Ordinal)
                     .ThenBy(t => t.Name, StringComparer.Ordinal))
        {
            Tools.Add(new ToolRow(
                t.Name,
                t.Description,
                t.Category,
                t.ReadOnly,
                t.ReadOnly ? "只读" : "写操作 · 需人工确认",
                t.Parameters.Count == 0 ? "无参数" : $"{t.Parameters.Count} 个参数",
                t.Parameters.Select(p => new ToolParamRow(
                    p.Name,
                    p.Type,
                    p.Required ? "必填" : "可选",
                    string.IsNullOrWhiteSpace(p.Description) ? "（无说明）" : p.Description!)).ToList()));
        }

        var readOnly = Tools.Count(t => t.ReadOnly);
        CountSummary = $"共 {Tools.Count} 个工具：只读 {readOnly} 个，写操作 {Tools.Count - readOnly} 个（写操作一律要你确认后才会执行）";
        EmptyNote = Tools.Count == 0 ? "注册表里还没有工具。" : "";

        // 尽量保持选中项不变（刷新后列表重建，不保留会"跳走"）；没有选中就默认选第一个，避免右侧空着
        SelectedTool = keep is null
            ? Tools.FirstOrDefault()
            : Tools.FirstOrDefault(t => t.Name == keep) ?? Tools.FirstOrDefault();
    }

    /// <summary>清单变化可能在后台线程触发，切回 UI 线程再改集合。</summary>
    private static void OnUi(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app is null || app.Dispatcher.CheckAccess()) action();
        else app.Dispatcher.BeginInvoke(action);
    }
}
