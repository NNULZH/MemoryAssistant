using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.Features.Commitment;
using MemoryAssistant.Infrastructure;

namespace MemoryAssistant.App.ViewModels;

/// <summary>承诺卡片（UI 展示）。</summary>
public partial class CommitmentCard : ObservableObject
{
    public required string SessionName { get; init; }
    public required string Date { get; init; }
    public required string Sender { get; init; }
    public required bool IsSelf { get; init; }
    public required string Content { get; init; }
    public required string MatchedText { get; init; }
    public string KindTag => IsSelf ? "我的承诺" : "他人承诺";

    /// <summary>会话 id + 时间：点卡片跳到对应聊天位置用。</summary>
    public string SessionId { get; init; } = "";
    public long CreateTime { get; init; }
}

/// <summary>承诺页 ViewModel：规则初筛候选卡片列表。</summary>
public partial class CommitmentsViewModel : ObservableObject
{
    private readonly AppServices _services;

    public CommitmentsViewModel(AppServices services)
    {
        _services = services;
        Refresh = new AutoRefreshPolicy(services.Settings.Ui.AutoRefreshSeconds);
    }

    /// <summary>自动刷新节流策略（V4.1）：切 Tab 不再无条件重扫；点「重新扫描」永远真扫。</summary>
    public AutoRefreshPolicy Refresh { get; }

    public ObservableCollection<CommitmentCard> Cards { get; } = [];

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "加载中...";

    /// <summary>切到本页时调用（V4.1）：距上次扫描不足配置的间隔就不重扫；想看最新就点「重新扫描」。</summary>
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
        StatusText = "扫描索引中的承诺候选...";
        try
        {
            var resp = await _services.Bridge.RequestAsync("rag_commitments",
                new Dictionary<string, object?> { ["limit"] = 200 }, timeoutSeconds: 60);
            if (!resp.Success || resp.Data is not System.Text.Json.JsonElement je ||
                !je.TryGetProperty("candidates", out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                StatusText = $"加载失败：{resp.Error ?? "无数据"}";
                return;
            }

            var candidates = CommitmentDataParser.Parse(arr);
            Cards.Clear();
            foreach (var c in candidates)
            {
                Cards.Add(new CommitmentCard
                {
                    SessionName = c.SessionName,
                    Date = c.Date,
                    Sender = c.Sender,
                    IsSelf = c.IsSelf,
                    Content = c.Content,
                    MatchedText = c.MatchedText,
                    SessionId = c.SessionId,
                    CreateTime = c.CreateTime,
                });
            }
            var selfCount = Cards.Count(x => x.IsSelf);
            StatusText = $"共 {Cards.Count} 条规则候选（我的 {selfCount} / 他人 {Cards.Count - selfCount}）· 待确认 · 可在“回忆”页用自然语言提问";
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
}
