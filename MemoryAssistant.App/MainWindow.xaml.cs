using System.Windows;
using MemoryAssistant.App.Views;
using MemoryAssistant.App.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace MemoryAssistant.App;

/// <summary>
/// 主窗口：Wpf.Ui NavigationView 导航壳。
/// 侧边栏：回忆 / 搜索 / 设置（TargetPageType 自动导航）。
/// 页面由 AppPageProvider 创建并注入子 ViewModel。
/// </summary>
public partial class MainWindow : FluentWindow
{
    private INavigationService? _navigationService;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 关窗 = **收起到后台**（服务与任务继续跑），不是退出。
    ///
    /// 为什么这么改：应用是长驻的（后台任务按周期操作微信），关掉窗口不该把服务一起带走；
    /// 而原来的行为更糟——最后一个窗口关掉后进程靠某个线程"挂着"，Dispatcher 已经停了，
    /// 于是全局热键收不到、界面也叫不回来，只能去任务管理器结束（实测踩过）。
    /// 现在明确成：关窗 → 隐藏；Ctrl+Alt+M 叫回来；Ctrl+Alt+Q 才是真正退出。
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!App.ShuttingDown)
        {
            e.Cancel = true;
            Hide();
            App.LogAction("[Action] 主窗口已收起（服务与任务继续在后台运行；"
                          + $"{App.ToggleWindowKeyText} 叫回界面，{App.QuitKeyText} 退出应用）");
            // 第一次收起到托盘时提示一次，让用户知道去哪儿找它
            App.NotifyTrayHintOnce();
        }
        base.OnClosing(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _navigationService = new NavigationService(new AppPageProvider((ViewModels.MainViewModel)DataContext!));
        _navigationService.SetNavigationControl(RootNavigation);
        _navigationService.Navigate(ResolveStartPage());
        // 导航完成后把"当前页面"告诉环境感知层（Agent 通过 get_ui_context 读它）
        RootNavigation.Navigated += (_, args) =>
        {
            var name = PageName(args.Page?.GetType());
            UiContext.Current.SetPage(name);
            // 离开任务页就不再"选中着某个任务"（否则 Agent 会拿一个过期的指代对象）
            if (name != "任务") UiContext.Current.SetMission(null);
            (DataContext as ViewModels.MainViewModel)?.RefreshShellStatus();
        };
        UiContext.Current.SetPage(PageName(ResolveStartPage()));
        (DataContext as ViewModels.MainViewModel)?.RefreshShellStatus();
    }

    /// <summary>
    /// 切页（供页面内的"快捷入口"按钮调用，例如首页的「查看知识库」）。
    /// 导航是窗口的职责，页面只发出意图，不去直接摸 NavigationView。
    /// </summary>
    public void NavigateTo(Type pageType)
    {
        _navigationService?.Navigate(pageType);
        UiContext.Current.SetPage(PageName(pageType));
    }

    /// <summary>页面类型 → 给 Agent 看的中文页名。</summary>
    private static string PageName(Type? pageType) => pageType switch
    {
        var t when t == typeof(OverviewView) => "首页",
        var t when t == typeof(ChatView) => "对话",
        var t when t == typeof(TaskView) => "任务",
        var t when t == typeof(KnowledgeView) => "知识库",
        var t when t == typeof(TimelineView) => "时间线",
        var t when t == typeof(TopicsView) => "话题",
        var t when t == typeof(ProfilesView) => "画像",
        var t when t == typeof(CommitmentsView) => "承诺",
        var t when t == typeof(EnvironmentView) => "环境",
        var t when t == typeof(WorkflowView) => "工作流",
        var t when t == typeof(ToolsView) => "工具",
        var t when t == typeof(SearchView) => "搜索",
        var t when t == typeof(SettingsView) => "设置",
        _ => "首页",
    };

    /// <summary>启动页可用 --page overview|chat|tasks|environment|workflow|tools|knowledge|timeline|commitments|topics|profiles|search|settings 指定（便于演示/自动化）。</summary>
    private static Type ResolveStartPage()
    {
        var args = Environment.GetCommandLineArgs();
        var idx = Array.IndexOf(args, "--page");
        var page = idx >= 0 && idx + 1 < args.Length ? args[idx + 1].ToLowerInvariant() : "overview";
        return page switch
        {
            "chat" => typeof(ChatView),
            "tasks" => typeof(TaskView),
            "environment" => typeof(EnvironmentView),
            "workflow" => typeof(WorkflowView),
            "tools" => typeof(ToolsView),
            "knowledge" => typeof(KnowledgeView),
            "timeline" => typeof(TimelineView),
            "commitments" => typeof(CommitmentsView),
            "topics" => typeof(TopicsView),
            "profiles" => typeof(ProfilesView),
            "search" => typeof(SearchView),
            "settings" => typeof(SettingsView),
            _ => typeof(OverviewView),
        };
    }
}
