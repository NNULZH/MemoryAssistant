using System.Windows;
using System.Windows.Controls;
using MemoryAssistant.Core.Agent.Action;
using MemoryAssistant.Core.Agent.Interaction;

namespace MemoryAssistant.App.Views;

/// <summary>
/// 统一的用户决策弹窗（详见 XAML 注释）。选项按钮在代码里按调用方给的列表生成。
/// </summary>
public partial class UserChoiceWindow : Wpf.Ui.Controls.FluentWindow
{
    /// <summary>用户的选择（取消时为 <see cref="UserChoice.Cancel"/>）。</summary>
    public UserChoice Choice { get; private set; } = UserChoice.Cancel();

    public UserChoiceWindow(UserChoiceRequest request)
    {
        InitializeComponent();

        if (!string.IsNullOrWhiteSpace(request.Title)) TitleText.Text = request.Title;
        QuestionText.Text = request.Question;
        ContextText.Text = request.Context;
        ContextBox.Visibility = string.IsNullOrWhiteSpace(request.Context)
            ? Visibility.Collapsed
            : Visibility.Visible;

        Button? focusTarget = null;
        Button? last = null;
        var index = 0;
        foreach (var option in request.Options)
        {
            var button = new Button
            {
                Content = option,
                MinWidth = 104,
                Height = 36,
                Margin = new Thickness(10, 0, 0, 0),
                Padding = new Thickness(18, 0, 18, 0),
                // 第一项 = 主操作（绿色填充）；其余是次要按钮——视觉上"哪个是主要动作"一眼可见
                Style = (Style)FindResource(index == 0 ? "AccentButtonStyle" : "SecondaryButtonStyle"),
                Tag = option,
            };
            button.Click += OnOptionClick;

            // 默认项优先获焦点（回车就走它）；没指定默认项就聚焦第一项
            if (string.Equals(option, request.DefaultOption, StringComparison.Ordinal)) button.IsDefault = true;
            if (button.IsDefault) focusTarget = button;
            focusTarget ??= button;

            OptionList.Items.Add(button);
            last = button;
            index++;
        }

        // Esc = 取消（不给选择，也不做任何动作）
        if (last is not null) last.IsCancel = true;

        Loaded += (_, _) =>
        {
            focusTarget?.Focus();
            RaiseToFront();
        };
    }

    /// <summary>
    /// 把"要你决定"的窗**拉到最前面**。
    ///
    /// 为什么必须显式做：这个弹窗的意义就是打断你，可它作为子窗只保证盖住主窗口——
    /// 实测确认窗曾被**刚刚被拉起的微信**整个盖住（写操作正是要操作微信，所以那一刻
    /// 微信恰好在前台），用户根本看不到在等他点，任务就这么干等到超时。
    /// 瞬时置顶再取消置顶是 WPF 里可靠的"抢前台"手法（只置顶会永久压住其他窗口）。
    /// </summary>
    private void RaiseToFront()
    {
        try
        {
            Activate();
            Topmost = true;
            Topmost = false;
            Activate();
        }
        catch
        {
            // 抢前台失败不能影响弹窗本身
        }
    }

    private void OnOptionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string option })
        {
            Choice = new UserChoice(option, false);
            DialogResult = true;
        }
        Close();
    }
}

/// <summary>
/// 界面层的"唯一用户交互口"实现（规范 §4）：
/// 弹窗问用户，把选择原样交回 Agent；写操作闸门（IActionConfirmation）也走它。
///
/// 测试旁路：环境变量 <c>MEMORYASSISTANT_AUTO_APPROVE_WRITES=1</c> 时，**只对写操作闸门**
/// 自动放行（不弹窗），并且每条都打日志留痕——绝不落配置文件，避免"以为有闸门其实被悄悄关掉"。
/// Agent 主动发起的 request_user_confirmation 一定弹窗（那正是要验证的能力）。
/// </summary>
public sealed class UiUserInteraction : IUserInteraction
{
    private static readonly bool AutoApproveWrites =
        Environment.GetEnvironmentVariable("MEMORYASSISTANT_AUTO_APPROVE_WRITES") == "1";

    private readonly Action<string>? _audit;

    public UiUserInteraction(Action<string>? audit = null) => _audit = audit;

    public async Task<UserChoice> RequestChoiceAsync(UserChoiceRequest request, CancellationToken ct = default)
    {
        if (AutoApproveWrites && request.Title == ActionConfirmationGate.WriteActionTitle)
        {
            _audit?.Invoke("[Action] ⚠ 自动授权（测试模式 MEMORYASSISTANT_AUTO_APPROVE_WRITES=1，无人确认）："
                           + request.Context);
            return new UserChoice(ActionConfirmationGate.ConfirmOption, false);
        }

        var app = Application.Current;
        if (app?.Dispatcher is null)
        {
            _audit?.Invoke("[Action] 没有可用界面，按取消处理（不替用户做主）。");
            return UserChoice.Cancel();
        }

        // 环境感知：弹窗期间 Agent 查询界面状态时能看到"有一个决定在等用户"
        UiContext.Current.SetPendingConfirmation(request.Question);
        UiContext.Current.SetDialog("用户决策弹窗");
        _audit?.Invoke($"[Action] 准备弹确认窗：{request.Question}（选项 {string.Join("/", request.Options)}）");

        // 等用户点确认这段时间**不算进任务预算**（他可能正在讲解/在犹豫）。
        // 不这样做的实测后果：确认窗开了 8 分钟才被点，任务早被判超时，用户点头后什么都不发生。
        using var waiting = MemoryAssistant.Core.Agent.Runtime.UserDecisionClock.Wait();

        var tcs = new TaskCompletionSource<UserChoice>();
        await app.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                _audit?.Invoke("[Action] 确认窗已投递到 UI 线程，开始 ShowDialog");
                var dialog = new UserChoiceWindow(request);
                if (app.MainWindow is { } owner && !ReferenceEquals(owner, dialog) && owner.IsLoaded)
                    dialog.Owner = owner;
                var ok = dialog.ShowDialog() == true;
                var choice = ok ? dialog.Choice : UserChoice.Cancel();
                _audit?.Invoke(choice.Cancelled
                    ? $"[Action] 用户取消：{request.Question}"
                    : $"[Action] 用户选择「{choice.Selection}」：{request.Question}");
                tcs.TrySetResult(choice);
            }
            catch (Exception ex)
            {
                _audit?.Invoke($"[Action] 决策弹窗出错，按取消处理：{ex.Message}");
                tcs.TrySetResult(UserChoice.Cancel());
            }
            finally
            {
                UiContext.Current.SetPendingConfirmation(null);
                UiContext.Current.SetDialog(null);
            }
        });
        return await tcs.Task;
    }
}
