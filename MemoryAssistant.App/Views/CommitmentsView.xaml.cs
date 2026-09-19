using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MemoryAssistant.App.ViewModels;

namespace MemoryAssistant.App.Views;

public partial class CommitmentsView : UserControl
{
    public CommitmentsView()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // V4.1：切到本页时走"按间隔节流"的入口，不再无条件重扫（想看最新点「重新扫描」）
        if (DataContext is CommitmentsViewModel vm && vm.RefreshIfStaleCommand.CanExecute(null))
            vm.RefreshIfStaleCommand.Execute(null);
    }

    /// <summary>点承诺卡片：弹开仿微信聊天记录，并定位到这条。</summary>
    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CommitmentCard card }) return;
        if (string.IsNullOrWhiteSpace(card.SessionId)) return;

        var time = card.CreateTime > 0
            ? System.DateTimeOffset.FromUnixTimeSeconds(card.CreateTime).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : card.Date;
        var item = new EvidenceItem(
            Index: 0,
            SessionName: card.SessionName,
            Time: time,
            Sender: card.Sender,
            Content: card.Content)
        {
            SessionId = card.SessionId,
            CreateTime = card.CreateTime,
            Date = card.Date,
        };
        new ConversationWindow(item).ShowDialog();
        e.Handled = true;
    }
}
