using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.Timeline;
using MemoryAssistant.Infrastructure;

namespace MemoryAssistant.App.ViewModels;

/// <summary>时间线中的一天（UI 包装，绑定用）。</summary>
public partial class TimelineDay : ObservableObject
{
    public required string Date { get; init; }
    public required int MsgCount { get; init; }
    public required int SessionCount { get; init; }
    public required IReadOnlyList<TimelineSession> Sessions { get; init; }
    public string Display => $"{Date}  ({MsgCount} 条 / {SessionCount} 会话)";
}

/// <summary>某日某会话的片段详情（UI 包装，绑定用）。</summary>
public partial class DayChunk : ObservableObject
{
    public required string SessionId { get; init; }
    public required string SessionName { get; init; }
    public required string Text { get; init; }
    public required int MsgCount { get; init; }
    /// <summary>片段所属日期（yyyy-MM-dd）：点卡片跳转时定位到这一天。</summary>
    public required string Date { get; init; }
    /// <summary>片段起始时间（Unix 秒）：能把高亮落到那一小段上。</summary>
    public long CreateTime { get; init; }
}

/// <summary>时间线页 ViewModel：按日期回溯聊天活动。</summary>
public partial class TimelineViewModel : ObservableObject
{
    private readonly AppServices _services;

    public TimelineViewModel(AppServices services)
    {
        _services = services;
        Refresh = new AutoRefreshPolicy(services.Settings.Ui.AutoRefreshSeconds);
    }

    /// <summary>自动刷新节流策略（V4.1）：切 Tab 不再无条件重拉；点「刷新」永远真拉。</summary>
    public AutoRefreshPolicy Refresh { get; }

    public ObservableCollection<string> Months { get; } = [];
    public ObservableCollection<TimelineDay> Days { get; } = [];
    public ObservableCollection<DayChunk> DayDetails { get; } = [];

    [ObservableProperty]
    private string? _selectedMonth;

    [ObservableProperty]
    private TimelineDay? _selectedDay;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "加载时间线...";

    partial void OnSelectedMonthChanged(string? value)
    {
        if (value is null) return;
        var month = value[..7]; // yyyy-MM
        Days.Clear();
        foreach (var day in _allDays.Where(d => d.Date.StartsWith(month, StringComparison.Ordinal)))
            Days.Add(day);
    }

    partial void OnSelectedDayChanged(TimelineDay? value)
    {
        _ = LoadDayDetailAsync(value);
    }

    private List<TimelineDay> _allDays = [];

    /// <summary>
    /// 切到本页时调用（V4.1）：距上次加载不足配置的间隔就不重拉——避免来回切 Tab 反复打接口。
    /// 想看最新的就点页面上的「刷新」（走 <see cref="LoadCommand"/>，永远真拉）。
    /// </summary>
    [RelayCommand]
    private async Task RefreshIfStaleAsync()
    {
        if (!Refresh.ShouldAutoLoad()) return;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsBusy = true;
        StatusText = "加载时间线...";
        try
        {
            var resp = await _services.Bridge.RequestAsync("rag_timeline", null, timeoutSeconds: 30);
            if (!resp.Success || resp.Data is not System.Text.Json.JsonElement je ||
                !je.TryGetProperty("days", out var days))
            {
                StatusText = $"加载失败：{resp.Error ?? "无数据"}";
                return;
            }

            var data = TimelineDataParser.ParseDays(days);
            _allDays = data.Days.Select(d => new TimelineDay
            {
                Date = d.Date,
                MsgCount = d.MsgCount,
                SessionCount = d.SessionCount,
                Sessions = d.Sessions,
            }).ToList();

            Months.Clear();
            foreach (var m in data.Months) Months.Add(m);
            SelectedMonth = Months.FirstOrDefault();
            StatusText = $"共 {_allDays.Count} 天 / {data.Months.Count} 个月（数据来自本地索引）";
            Refresh.MarkLoaded();
        }
        catch (Exception ex)
        {
            StatusText = $"加载异常：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadDayDetailAsync(TimelineDay? day)
    {
        if (day is null) return;
        DayDetails.Clear();
        try
        {
            var resp = await _services.Bridge.RequestAsync("rag_day_detail", new Dictionary<string, object?>
            {
                ["date"] = day.Date,
            }, timeoutSeconds: 30);
            if (!resp.Success || resp.Data is not System.Text.Json.JsonElement je ||
                !je.TryGetProperty("chunks", out var chunks))
                return;

            foreach (var c in TimelineDataParser.ParseDayChunks(chunks))
            {
                DayDetails.Add(new DayChunk
                {
                    SessionId = c.SessionId,
                    SessionName = c.SessionName,
                    Text = c.Text,
                    MsgCount = c.MsgCount,
                    Date = string.IsNullOrWhiteSpace(c.Date) ? day.Date : c.Date,
                    CreateTime = c.CreateTime,
                });
            }
        }
        catch
        {
            // 忽略详情加载失败
        }
    }
}
