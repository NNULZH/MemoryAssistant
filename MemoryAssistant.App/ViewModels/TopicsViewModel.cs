using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.Features.Topics;
using MemoryAssistant.Infrastructure;

namespace MemoryAssistant.App.ViewModels;

/// <summary>主题行（UI 展示）。</summary>
public partial class TopicRow : ObservableObject
{
    public required NamedTopic Topic { get; init; }
    public string Display => $"{Topic.Label}  ({Topic.TotalSize})";
    public string KindTag => Topic.Kind;
    public IReadOnlyList<DayCount> PerDay => Topic.PerDay;
    public IReadOnlyList<TopicSample> Samples => Topic.Samples;
    public string Representative => Topic.Representative;
}

/// <summary>话题页 ViewModel：关键词分类 + Embedding 聚类 + LLM 命名。</summary>
public partial class TopicsViewModel : ObservableObject
{
    private readonly AppServices _services;

    public TopicsViewModel(AppServices services)
    {
        _services = services;
        Refresh = new AutoRefreshPolicy(services.Settings.Ui.AutoRefreshSeconds);
    }

    /// <summary>
    /// 自动刷新节流策略（V4.1）。本页是**最贵**的一个：每次分析都要跑聚类 + 让模型给主题命名，
    /// 所以原来"一切到这个 Tab 就自动分析一遍"很容易白烧额度——现在按间隔节流，手动点「分析」则照旧立即跑。
    /// </summary>
    public AutoRefreshPolicy Refresh { get; }

    public ObservableCollection<TopicRow> Topics { get; } = [];
    public ObservableCollection<DayCount> SelectedPerDay { get; } = [];
    public ObservableCollection<TopicSample> SelectedSamples { get; } = [];

    public IReadOnlyList<int> DayOptions { get; } = [7, 14, 30];

    [ObservableProperty]
    private int _recentDays = 14;

    [ObservableProperty]
    private TopicRow? _selectedTopic;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "选择天数后点击“分析”。";

    partial void OnSelectedTopicChanged(TopicRow? value)
    {
        SelectedPerDay.Clear();
        SelectedSamples.Clear();
        if (value is null) return;
        foreach (var d in value.PerDay) SelectedPerDay.Add(d);
        foreach (var s in value.Samples) SelectedSamples.Add(s);
    }

    /// <summary>
    /// 切到本页时调用（V4.1）：距上次分析不足配置的间隔就不重跑（本页会花 LLM 额度）。
    /// 想看最新就手动点「分析」。
    /// </summary>
    [RelayCommand]
    private async Task RefreshIfStaleAsync()
    {
        if (!Refresh.ShouldAutoLoad()) return;
        await AnalyzeAsync();
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = $"分析最近 {RecentDays} 天（聚类 + 命名）...";
        try
        {
            var service = new TopicAnalysisService(_services.Bridge, _services.Chat!, _services.Logger);
            var result = await service.AnalyzeAsync(RecentDays);

            Topics.Clear();
            foreach (var t in result.Topics)
                Topics.Add(new TopicRow { Topic = t });

            SelectedTopic = Topics.FirstOrDefault();
            StatusText = $"最近 {RecentDays} 天 · {result.Raw.ChunksInScope} 个片段 · {Topics.Count} 个主题（关键词 + 聚类命名）";
            Refresh.MarkLoaded();
        }
        catch (Exception ex)
        {
            StatusText = $"分析异常：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
