using System.Windows;
using MemoryAssistant.App.ViewModels;

namespace MemoryAssistant.App.Views;

/// <summary>
/// 首页代码后置：只做视图层面的事——进页面刷新真实统计、点入口切页、点建议直接把话发给 Agent。
/// </summary>
public partial class OverviewView : System.Windows.Controls.UserControl
{
    public OverviewView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 每次导航进入都刷新一次（AppPageProvider 每次 new 页面，状态不跨导航保留）
        if (DataContext is OverviewViewModel vm && !vm.IsBusy)
            await vm.RefreshCommand.ExecuteAsync(null);
    }

    private void GoChat_Click(object sender, RoutedEventArgs e) => Go(typeof(ChatView));
    private void GoWorkflow_Click(object sender, RoutedEventArgs e) => Go(typeof(WorkflowView));
    private void GoKnowledge_Click(object sender, RoutedEventArgs e) => Go(typeof(KnowledgeView));
    private void GoTools_Click(object sender, RoutedEventArgs e) => Go(typeof(ToolsView));
    private void GoMissions_Click(object sender, RoutedEventArgs e) => Go(typeof(TaskView));
    private void GoEnvironment_Click(object sender, RoutedEventArgs e) => Go(typeof(EnvironmentView));

    /// <summary>点一条建议 → 切到对话页并把这句话发出去（用户不用自己组织语言）。</summary>
    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: string text }) return;
        Go(typeof(ChatView));
        if (DataContext is OverviewViewModel vm && vm.UseSuggestionCommand.CanExecute(text))
            vm.UseSuggestionCommand.Execute(text);
    }

    private void Go(Type pageType)
        => (Window.GetWindow(this) as MainWindow)?.NavigateTo(pageType);
}
