using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MemoryAssistant.App.ViewModels;
using MemoryAssistant.Core.Agent.Conversation;
using MemoryAssistant.Core.Agent.Skills;

namespace MemoryAssistant.App;

/// <summary>
/// 会话详情窗口（仿微信）：点卡片落到真实聊天记录里，显示当天整段对话并高亮定位到具体那一条。
/// 文字、表情、图片都尽量原样呈现（图片/语音是 welive 解密后的本地文件）；
/// 解不出来或暂不支持的类型会如实标注，不装作没有。
/// 关掉窗口只是关掉这层遮罩，主界面（含滚动位置）原样保留。
/// </summary>
public partial class ConversationWindow : Window
{
    private static readonly MediaPlayer Voice = new();   // 单例播放器，避免多条语音叠着播

    public ConversationWindow(EvidenceItem item)
    {
        InitializeComponent();

        TitleText.Text = string.IsNullOrWhiteSpace(item.SessionName) ? "聊天记录" : item.SessionName;
        SubText.Text = string.IsNullOrWhiteSpace(item.Time) ? "" : item.Time;
        TargetText.Text = string.IsNullOrWhiteSpace(item.Sender)
            ? Preview(item.Content)
            : $"{item.Sender}：{Preview(item.Content)}";
        StatusText.Text = "正在读取当天完整对话…";

        Loaded += async (_, _) =>
        {
            PlayEnterAnimation();
            await LoadAsync(item);
        };
    }

    private async Task LoadAsync(EvidenceItem item)
    {
        var backend = App.Services?.MemoryBackend;
        if (backend is null || string.IsNullOrWhiteSpace(item.SessionId))
        {
            StatusText.Text = "当前环境读不到本地聊天库（Bridge 未就绪）。";
            return;
        }

        var day = DayOf(item);
        var begin = new DateTimeOffset(day, TimeSpan.FromHours(8)).ToUnixTimeSeconds();
        var end = begin + 86400;

        var messages = await SafeReadRenderedAsync(backend, item.SessionId, begin, end);
        if (messages.Count == 0)
        {
            // 退化路径：拿不到"可渲染"的消息时，至少把当天文本读出来（不至于空白）
            var plain = await SafeReadPlainAsync(backend, item.SessionId, day);
            messages = plain.Select(m => new RenderedMessage
            {
                CreateTime = m.CreateTime,
                SenderName = m.SenderName,
                Text = m.Content,
                Kind = "text",
            }).ToList();
        }

        var lines = ConversationView.Build(messages, item.CreateTime, item.Content);
        MessageList.ItemsSource = lines;

        var target = lines.FirstOrDefault(l => l.IsTarget);
        StatusText.Text = $"{day:yyyy-MM-dd} · 共 {lines.Count} 条"
            + (target is null ? "（没定位到那条的确切位置，已显示当天全部）" : "");
        if (target is not null)
        {
            MessageList.SelectedItem = target;
            MessageList.ScrollIntoView(target);
        }
    }

    private static DateTime DayOf(EvidenceItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Date) &&
            DateTime.TryParse(item.Date, out var parsed)) return parsed.Date;
        if (item.CreateTime > 0) return DateTimeOffset.FromUnixTimeSeconds(item.CreateTime).ToLocalTime().Date;
        return DateTime.Today;
    }

    /// <summary>定位卡里的预览：片段文本可能是几百字，截断到一眼能看完。</summary>
    private static string Preview(string text)
    {
        var t = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return t.Length <= 160 ? t : t[..160] + "…";
    }

    private static async Task<IReadOnlyList<RenderedMessage>> SafeReadRenderedAsync(
        IMemoryBackend backend, string sessionId, long begin, long end)
    {
        try
        {
            return await backend.ReadRenderedAsync(sessionId, begin, end, 500, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Conversation] read_rendered 失败：{ex.Message}");
            return [];
        }
    }

    private static async Task<IReadOnlyList<Core.Agent.Evidence>> SafeReadPlainAsync(
        IMemoryBackend backend, string sessionId, DateTime day)
    {
        try
        {
            return await backend.ReadDayMessagesAsync(sessionId, day.ToString("yyyy-MM-dd"), 200, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Conversation] read_messages 失败：{ex.Message}");
            return [];
        }
    }

    /// <summary>点图片：用系统看图打开原图（大图查看）。</summary>
    private void Image_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Image { DataContext: ConversationLine { ImagePath: { Length: > 0 } path } })
            OpenPath(path);
        e.Handled = true;
    }

    private void PlayVoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ConversationLine { AudioPath: { Length: > 0 } path } })
        {
            try
            {
                Voice.Open(new Uri(path));
                Voice.Play();
                StatusText.Text = $"正在播放语音：{System.IO.Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"语音播放失败：{ex.Message}";
            }
        }
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打不开文件：{ex.Message}", "聊天记录");
        }
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
        ((TranslateTransform)DialogCard.RenderTransform)
            .BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void Overlay_Click(object sender, MouseButtonEventArgs e) => Close();

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
