using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.Features.Profiles;
using MemoryAssistant.Core.Features.Topics;
using MemoryAssistant.Infrastructure;

namespace MemoryAssistant.App.ViewModels;

/// <summary>会话画像行（UI 展示）。</summary>
public partial class ProfileRow : ObservableObject
{
    public required SessionProfile Profile { get; init; }
    public string Display => $"{Profile.SessionName}  ({Profile.MsgCount} 条 / {Profile.DayCount} 天)";
}

/// <summary>最近消息（UI 展示）。带会话 id + 时间，点卡片能跳到那条。</summary>
public sealed record RecentMessage(string Time, string Sender, string Content)
{
    public string SessionId { get; init; } = "";
    public long CreateTime { get; init; }
}

/// <summary>画像页 ViewModel：会话级数据统计。</summary>
public partial class ProfilesViewModel : ObservableObject
{
    private readonly AppServices _services;

    public ProfilesViewModel(AppServices services)
    {
        _services = services;
        Refresh = new AutoRefreshPolicy(services.Settings.Ui.AutoRefreshSeconds);
    }

    /// <summary>自动刷新节流策略（V4.1）：切 Tab 不再无条件重拉；点「刷新」永远真拉。</summary>
    public AutoRefreshPolicy Refresh { get; }

    public ObservableCollection<ProfileRow> Profiles { get; } = [];
    public ObservableCollection<ProfileRow> FilteredProfiles { get; } = [];
    public ObservableCollection<KeywordTopic> DetailTopics { get; } = [];
    public ObservableCollection<RecentMessage> RecentMessages { get; } = [];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private ProfileRow? _selectedProfile;

    [ObservableProperty]
    private string? _detailSummary;

    [ObservableProperty]
    private string? _activeHoursText;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "加载中...";

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnSelectedProfileChanged(ProfileRow? value)
    {
        _ = LoadDetailAsync(value);
    }

    private void ApplyFilter()
    {
        FilteredProfiles.Clear();
        var kw = SearchText.Trim();
        foreach (var p in Profiles)
        {
            if (string.IsNullOrEmpty(kw) ||
                p.Profile.SessionName.Contains(kw, StringComparison.OrdinalIgnoreCase))
                FilteredProfiles.Add(p);
        }
        if (SelectedProfile is not null && !FilteredProfiles.Contains(SelectedProfile))
            SelectedProfile = null;
    }

    /// <summary>
    /// 切到本页时调用（V4.1）：距上次加载不足配置的间隔就不重拉。
    /// 想看最新的就点页面上的「刷新」。
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
        StatusText = "加载会话画像...";
        try
        {
            var resp = await _services.Bridge.RequestAsync("rag_profiles",
                new Dictionary<string, object?> { ["top"] = 200 }, timeoutSeconds: 30);
            if (!resp.Success || resp.Data is not System.Text.Json.JsonElement je ||
                !je.TryGetProperty("profiles", out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                StatusText = $"加载失败：{resp.Error ?? "无数据"}";
                return;
            }

            var profiles = ProfileDataParser.Parse(arr);
            Profiles.Clear();
            FilteredProfiles.Clear();
            foreach (var p in profiles)
            {
                var row = new ProfileRow { Profile = p };
                Profiles.Add(row);
                FilteredProfiles.Add(row);
            }
            StatusText = $"共 {Profiles.Count} 个会话画像（数据来自本地索引）";
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

    private async Task LoadDetailAsync(ProfileRow? row)
    {
        if (row is null) return;
        DetailTopics.Clear();
        RecentMessages.Clear();
        ActiveHoursText = null;
        DetailSummary = null;
        var p = row.Profile;

        DetailSummary = $"消息总数：{p.MsgCount}  ·  聊天天数：{p.DayCount}\n日期范围：{p.FirstDate} ~ {p.LastDate}";
        foreach (var t in p.TopKeywordTopics) DetailTopics.Add(t);

        // 活跃时段与最近消息：一次 read_messages 两用（样本：最近 200 条）
        try
        {
            var resp = await _services.Bridge.RequestAsync("read_messages",
                new Dictionary<string, object?>
                {
                    ["session_id"] = p.SessionId,
                    ["limit"] = 200,
                }, timeoutSeconds: 30);
            if (resp.Success && resp.Data is System.Text.Json.JsonElement arr &&
                arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var times = new List<long>();
                foreach (var m in arr.EnumerateArray())
                {
                    if (m.TryGetProperty("create_time", out var t) && t.GetInt64() > 0)
                        times.Add(t.GetInt64());
                }
                var hist = HourlyHistogram.Compute(times);
                var top = HourlyHistogram.TopHours(hist, 3);
                ActiveHoursText = top.Count == 0
                    ? "无活跃时段样本"
                    : string.Join(" · ", top.Select(h => $"{h.Hour:D2}-{h.Hour + 1:D2}点 {h.Count}条"));

                // 最近 5 条
                foreach (var m in arr.EnumerateArray().TakeLast(5).Reverse())
                {
                    var ct = m.TryGetProperty("create_time", out var ctv) ? ctv.GetInt64() : 0L;
                    var time = ct > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(ct).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
                    var sender = m.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? "" : "";
                    var content = m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                    RecentMessages.Add(new RecentMessage(time, sender, content)
                    {
                        SessionId = p.SessionId,
                        CreateTime = ct,
                    });
                }
            }
        }
        catch
        {
            ActiveHoursText = "活跃时段加载失败";
        }
    }
}
