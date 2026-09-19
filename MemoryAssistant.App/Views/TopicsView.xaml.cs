using System.Windows;
using System.Windows.Controls;
using MemoryAssistant.App.ViewModels;
using MemoryAssistant.Core.Features.Topics;

namespace MemoryAssistant.App.Views;

public partial class TopicsView : UserControl
{
    public TopicsView()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // V4.1：切到本页时走"按间隔节流"的入口——本页分析会花 LLM 额度，不再无条件重跑
        if (DataContext is TopicsViewModel vm && vm.RefreshIfStaleCommand.CanExecute(null))
            vm.RefreshIfStaleCommand.Execute(null);
    }

    private void Sample_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TopicSample s }) return;
        if (string.IsNullOrWhiteSpace(s.SessionId)) return;

        var time = s.CreateTime > 0
            ? DateTimeOffset.FromUnixTimeSeconds(s.CreateTime).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : s.Date;
        var item = new EvidenceItem(
            Index: 0,
            SessionName: s.SessionName,
            Time: time,
            Sender: "",
            Content: s.Text)
        {
            SessionId = s.SessionId,
            CreateTime = s.CreateTime,
            Date = s.Date,
        };
        new ConversationWindow(item).ShowDialog();
    }
}
