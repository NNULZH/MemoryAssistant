using System.Windows;
using System.Windows.Input;
using MemoryAssistant.App.ViewModels;

namespace MemoryAssistant.App.Views;

/// <summary>知识库页代码后置：进页面自动读一次索引状态；检索框支持 Enter 直接查。</summary>
public partial class KnowledgeView : System.Windows.Controls.UserControl
{
    public KnowledgeView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is KnowledgeViewModel vm && !vm.IsIndexBusy)
            await vm.RefreshStatusCommand.ExecuteAsync(null);
    }

    private void Query_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is KnowledgeViewModel vm && vm.SearchCommand.CanExecute(null))
        {
            vm.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }
}
