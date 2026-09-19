using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MemoryAssistant.App.ViewModels;

namespace MemoryAssistant.App.Views;

public partial class TimelineView : UserControl
{
    public TimelineView()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // V4.1：切到本页时走"按间隔节流"的入口，不再无条件重拉（想看最新点页面上的「刷新」）
        if (DataContext is TimelineViewModel vm && vm.RefreshIfStaleCommand.CanExecute(null))
            vm.RefreshIfStaleCommand.Execute(null);
    }

    /// <summary>点当天的会话片段：弹开仿微信聊天记录，并高亮到这一段。</summary>
    private void Chunk_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DayChunk chunk }) return;
        if (string.IsNullOrWhiteSpace(chunk.SessionId)) return;

        var time = chunk.CreateTime > 0
            ? System.DateTimeOffset.FromUnixTimeSeconds(chunk.CreateTime).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : chunk.Date;
        var item = new EvidenceItem(
            Index: 0,
            SessionName: chunk.SessionName,
            Time: time,
            Sender: "",
            Content: chunk.Text)
        {
            SessionId = chunk.SessionId,
            CreateTime = chunk.CreateTime,
            Date = chunk.Date,
        };
        new ConversationWindow(item).ShowDialog();
        e.Handled = true;
    }
}
