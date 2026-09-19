using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using MemoryAssistant.App.ViewModels;

namespace MemoryAssistant.App;

/// <summary>证据详情窗口：遮罩式弹窗（点空白/Esc/按钮关闭 + 淡入上滑动画）。</summary>
public partial class EvidenceWindow : Window
{
    public EvidenceWindow(EvidenceItem item)
    {
        InitializeComponent();
        IndexText.Text = $"[{item.Index}]";
        SessionText.Text = item.SessionName;
        MetaText.Text = string.Join("  ", new[]
        {
            item.Time,
            string.IsNullOrEmpty(item.Sender) ? null : $"发送者: {item.Sender}",
        }.Where(s => !string.IsNullOrEmpty(s)));
        ContentText.Text = item.Content;

        Loaded += (_, _) => PlayEnterAnimation();
    }

    private void PlayEnterAnimation()
    {
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        var slide = new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        DialogCard.BeginAnimation(OpacityProperty, fade);
        ((System.Windows.Media.TranslateTransform)DialogCard.RenderTransform)
            .BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
    }

    // 点击遮罩空白处关闭
    private void Overlay_Click(object sender, MouseButtonEventArgs e) => Close();

    // 内容卡片：拦截冒泡，点击卡片本身不关闭
    private void Card_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
