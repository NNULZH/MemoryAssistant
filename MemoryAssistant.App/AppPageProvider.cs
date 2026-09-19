using MemoryAssistant.App.ViewModels;
using MemoryAssistant.App.Views;
using Wpf.Ui.Abstractions;

namespace MemoryAssistant.App;

/// <summary>页面提供器：导航到页面时创建实例并注入对应子 ViewModel。</summary>
public sealed class AppPageProvider : INavigationViewPageProvider
{
    private readonly MainViewModel _vm;

    public AppPageProvider(MainViewModel vm) => _vm = vm;

    public object? GetPage(Type pageType)
    {
        return pageType switch
        {
            // 课程展示版：首页（Overview）与三个"让能力被看见"的页面
            var t when t == typeof(OverviewView) => new OverviewView { DataContext = _vm.Overview },
            var t when t == typeof(EnvironmentView) => new EnvironmentView { DataContext = _vm.Environment },
            var t when t == typeof(WorkflowView) => new WorkflowView { DataContext = _vm.Workflow },
            var t when t == typeof(ToolsView) => new ToolsView { DataContext = _vm.Tools },
            var t when t == typeof(KnowledgeView) => new KnowledgeView { DataContext = _vm.Knowledge },
            var t when t == typeof(ChatView) => new ChatView { DataContext = _vm.Chat },
            var t when t == typeof(TaskView) => new TaskView { DataContext = _vm.Missions },
            var t when t == typeof(TimelineView) => new TimelineView { DataContext = _vm.Timeline },
            var t when t == typeof(CommitmentsView) => new CommitmentsView { DataContext = _vm.Commitments },
            var t when t == typeof(TopicsView) => new TopicsView { DataContext = _vm.Topics },
            var t when t == typeof(ProfilesView) => new ProfilesView { DataContext = _vm.Profiles },
            var t when t == typeof(SearchView) => new SearchView { DataContext = _vm.Search },
            var t when t == typeof(SettingsView) => new SettingsView { DataContext = _vm.Settings },
            _ => null,
        };
    }
}
