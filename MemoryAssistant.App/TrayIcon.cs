using System.Drawing;
using System.Windows;

namespace MemoryAssistant.App;

/// <summary>
/// 托盘图标：应用是**长驻**的（关窗后服务与任务继续跑），托盘就是它唯一的常驻入口——
/// 用户从这里显示窗口、一键停任务、退出，不必再去任务管理器。
///
/// 也兼任"系统通知"的出口（气泡提示 = Windows 通知，点击把窗口叫回来）：
/// 长期任务跑出值得一看的结论时，由通知智能体决定要不要通过它打断用户。
///
/// 实现用内置的 <c>System.Windows.Forms.NotifyIcon</c>（WPF-UI 4.x 没有托盘控件）；
/// 全名限定是为了避开 <c>System.Windows.*</c> 的同名类型（Application/Brush 等）。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly Action<string, string> _onBalloonClick;
    private bool _disposed;

    /// <summary>托盘菜单上的"停止所有任务"要显示有几个在跑（由外部刷新）。</summary>
    private readonly System.Windows.Forms.ToolStripMenuItem _stopItem = new("停止所有正在运行的任务");

    public TrayIcon(Action onShowWindow, Action onStopAllMissions, Action onQuit, Action<string, string> onBalloonClick)
    {
        _onBalloonClick = onBalloonClick;

        var menu = new System.Windows.Forms.ContextMenuStrip();
        var showItem = new System.Windows.Forms.ToolStripMenuItem("显示主窗口");
        showItem.Click += (_, _) => onShowWindow();
        menu.Items.Add(showItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        _stopItem.Click += (_, _) => onStopAllMissions();
        menu.Items.Add(_stopItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var quitItem = new System.Windows.Forms.ToolStripMenuItem("退出 MemoryAssistant");
        quitItem.Click += (_, _) => onQuit();
        menu.Items.Add(quitItem);

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "MemoryAssistant（后台运行中）",
            Visible = true,
            ContextMenuStrip = menu,
        };
        // 双击 = 显示主窗口（和大多数托盘程序一致）
        _icon.DoubleClick += (_, _) => onShowWindow();
        // 点气泡 = 也把窗口叫回来：通知的意义就是"让你去看一眼"
        _icon.BalloonTipClicked += (_, _) => _onBalloonClick(_lastTitle, _lastBody);
        _icon.BalloonTipTitle = "MemoryAssistant";
    }

    private string _lastTitle = "";
    private string _lastBody = "";

    /// <summary>
    /// 弹一条系统通知（Windows 托盘气泡）。空标题会被兜底，避免出现一条没有来源的气泡。
    /// </summary>
    public void Notify(string title, string body)
    {
        if (_disposed) return;
        _lastTitle = string.IsNullOrWhiteSpace(title) ? "MemoryAssistant" : title.Trim();
        _lastBody = (body ?? "").Trim();
        try
        {
            _icon.BalloonTipTitle = _lastTitle;
            _icon.BalloonTipText = _lastBody.Length > 250 ? _lastBody[..250] + "…" : _lastBody;
            _icon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
            _icon.ShowBalloonTip(8000);
        }
        catch
        {
            // 通知弹不出来不能影响主流程
        }
    }

    /// <summary>刷新托盘提示/菜单文案（有几个任务在跑，一眼能看出来）。</summary>
    public void SetRunningState(int runningCount)
    {
        if (_disposed) return;
        _icon.Text = runningCount > 0
            ? $"MemoryAssistant（后台运行中 · {runningCount} 个任务在跑）"
            : "MemoryAssistant（后台运行中）";
        _stopItem.Text = runningCount > 0
            ? $"停止所有正在运行的任务（{runningCount}）"
            : "停止所有正在运行的任务";
    }

    /// <summary>取 exe 自身的图标（不依赖 Assets 的运行时路径）。</summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var extracted = Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null) return extracted;
            }
        }
        catch
        {
            // 取不到就用系统默认图标
        }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
