using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MemoryAssistant.App.ViewModels;

namespace MemoryAssistant.App.Views;

public partial class ProfilesView : UserControl
{
    public ProfilesView()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // V4.1：切到本页时走"按间隔节流"的入口，不再无条件重拉（想看最新点页面上的「刷新」）
        if (DataContext is ProfilesViewModel vm && vm.RefreshIfStaleCommand.CanExecute(null))
            vm.RefreshIfStaleCommand.Execute(null);
    }

    /// <summary>点"最近消息"：弹开仿微信聊天记录，并定位到这一条。</summary>
    private void Recent_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RecentMessage m }) return;
        if (string.IsNullOrWhiteSpace(m.SessionId)) return;

        var sessionName = (DataContext as ProfilesViewModel)?.SelectedProfile?.Profile.SessionName ?? "";
        var item = new EvidenceItem(
            Index: 0,
            SessionName: sessionName,
            Time: m.Time,
            Sender: m.Sender,
            Content: m.Content)
        {
            SessionId = m.SessionId,
            CreateTime = m.CreateTime,
            Date = m.CreateTime > 0
                ? System.DateTimeOffset.FromUnixTimeSeconds(m.CreateTime).ToLocalTime().ToString("yyyy-MM-dd")
                : "",
        };
        new ConversationWindow(item).ShowDialog();
        e.Handled = true;
    }
}
