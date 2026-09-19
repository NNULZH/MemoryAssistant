using System.Windows;
using System.Windows.Input;
using MemoryAssistant.App.ViewModels;

namespace MemoryAssistant.App.Views;

public partial class SearchView : System.Windows.Controls.UserControl
{
    public SearchView()
    {
        InitializeComponent();
    }

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is SearchViewModel vm && vm.SearchCommand.CanExecute(null))
        {
            vm.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>点搜索结果：弹开仿微信聊天记录，并定位到这条消息。</summary>
    private void Result_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SearchResultItem r }) return;
        if (string.IsNullOrWhiteSpace(r.SessionId)) return;

        var item = new EvidenceItem(
            Index: 0,
            SessionName: r.SessionName,
            Time: r.Time,
            Sender: r.Sender,
            Content: r.Content)
        {
            SessionId = r.SessionId,
            CreateTime = r.CreateTime,
            Date = r.Date,
        };
        new ConversationWindow(item).ShowDialog();
        e.Handled = true;
    }
}
