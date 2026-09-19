using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoryAssistant.Infrastructure;

namespace MemoryAssistant.App.ViewModels;

/// <summary>一条搜索结果。</summary>
public partial class SearchResultItem : ObservableObject
{
    public required string SessionName { get; init; }
    public required string Time { get; init; }
    public required string Sender { get; init; }
    public required string Content { get; init; }
    public required string SessionId { get; init; }
    /// <summary>消息时间（Unix 秒）：点卡片跳到对应聊天位置用。</summary>
    public long CreateTime { get; init; }
    /// <summary>消息所在日期（yyyy-MM-dd）。</summary>
    public string Date { get; init; } = "";
}

/// <summary>全文搜索页 ViewModel（走 bridge search_messages）。</summary>
public partial class SearchViewModel : ObservableObject
{
    private readonly AppServices _services;

    public SearchViewModel(AppServices services) => _services = services;

    public ObservableCollection<SearchResultItem> Results { get; } = [];

    [ObservableProperty]
    private string _keyword = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "输入关键词开始全文搜索";

    [RelayCommand]
    private async Task SearchAsync()
    {
        var kw = Keyword.Trim();
        if (string.IsNullOrEmpty(kw) || IsBusy) return;

        IsBusy = true;
        StatusText = "搜索中...";
        Results.Clear();
        try
        {
            var resp = await _services.Bridge.RequestAsync("search_messages", new Dictionary<string, object?>
            {
                ["keyword"] = kw,
                ["limit"] = 50,
            }, timeoutSeconds: 60);

            if (!resp.Success)
            {
                StatusText = $"搜索失败：{resp.Error}";
                return;
            }
            if (resp.Data is not System.Text.Json.JsonElement je ||
                je.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                StatusText = "无结果";
                return;
            }

            var count = 0;
            foreach (var m in je.EnumerateArray())
            {
                var createTime = m.TryGetProperty("create_time", out var t) ? t.GetInt64() : 0L;
                var time = createTime > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(createTime).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    : "";
                Results.Add(new SearchResultItem
                {
                    SessionName = m.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? "" : "",
                    Time = time,
                    Sender = m.TryGetProperty("display_name", out var s) ? s.GetString() ?? "" : "",
                    Content = m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
                    SessionId = m.TryGetProperty("session_id", out var sid) ? sid.GetString() ?? "" : "",
                    CreateTime = createTime,
                    Date = createTime > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(createTime).ToLocalTime().ToString("yyyy-MM-dd")
                        : "",
                });
                count++;
            }
            StatusText = $"共 {count} 条结果";
        }
        catch (Exception ex)
        {
            StatusText = $"搜索异常：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
