using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using MemoryAssistant.App.ViewModels;

namespace MemoryAssistant.App.Views;

public partial class ChatView : System.Windows.Controls.UserControl
{
    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ChatViewModel oldVm)
        {
            if (oldVm.Messages is INotifyCollectionChanged oldC)
                oldC.CollectionChanged -= OnMessagesChanged;
            oldVm.StreamingTick -= OnStreamingTick;
        }
        if (e.NewValue is ChatViewModel vm)
        {
            if (vm.Messages is INotifyCollectionChanged c)
                c.CollectionChanged += OnMessagesChanged;
            vm.StreamingTick += OnStreamingTick;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 新消息加入后自动滚到底（发送/收到都触发）。
        if (e.Action == NotifyCollectionChangedAction.Add)
            Scroll.ScrollToEnd();
    }

    /// <summary>流式正文有增量 → 跟着往下滚，让用户看到字在往外冒。</summary>
    private void OnStreamingTick() => Scroll.ScrollToEnd();

    /// <summary>点证据卡片：跳到那段聊天里，并定位到你点的那一条（能读本地库时）。</summary>
    private void Evidence_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: ViewModels.EvidenceItem item })
        {
            new ConversationWindow(item).ShowDialog();
        }
    }

    /// <summary>Enter 发送、Shift+Enter 换行（聊天软件的通用习惯，别再要求按 Ctrl）。</summary>
    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) return;   // Shift+Enter 走换行

        if (DataContext is ChatViewModel vm && vm.SendCommand.CanExecute(null))
        {
            vm.SendCommand.Execute(null);
            e.Handled = true;
        }
    }
}
