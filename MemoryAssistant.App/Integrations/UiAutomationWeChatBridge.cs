using System.Diagnostics;
using System.Windows.Automation;
using MemoryAssistant.Core.Integrations;

namespace MemoryAssistant.App.Integrations;

/// <summary>
/// 微信桌面窗口只读桥（V3.3，Windows UIAutomation 实现）：
/// - 找窗口：按类名/标题/**进程名（Weixin/WeChat，兼容 4.x）**定位微信主窗（只枚举，不激活、不点击）；
/// - 读聊天：收集窗口中文本元素，返回最近若干条（去重、去空），交给 Skill 过滤噪音。
/// 全程只读，不影响用户操作；异常一律吞掉并按"读不到"返回，避免拖垮 Agent。
/// </summary>
public sealed class UiAutomationWeChatBridge : IWeChatWindowBridge
{
    private static readonly string[] ClassNames = ["WeChatMainWndForPC", "WeChatLoginWndForPC"];
    private static readonly string[] Titles = ["微信", "WeChat"];

    public bool IsAvailable
    {
        get
        {
            try
            {
                if (FindMainWindow() is not null) return true;
            }
            catch { /* 落到 Win32 枚举 */ }
            // UIA 看不到托盘/最小化的窗口，但它确实在——这也算"微信在运行"
            return WeChatWindowFinder.FindHandle() != IntPtr.Zero;
        }
    }

    public Task<IReadOnlyList<WindowInfo>> ListWeChatWindowsAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<WindowInfo>>(() =>
        {
            var list = new List<WindowInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var root = AutomationElement.RootElement;
                foreach (AutomationElement child in root.FindAll(TreeScope.Children, Condition.TrueCondition))
                {
                    if (ct.IsCancellationRequested) break;
                    var cls = child.Current.ClassName ?? "";
                    var title = child.Current.Name ?? "";
                    var procId = child.Current.ProcessId;
                    if (IsWeChatWindow(cls, title, procId) && seen.Add($"{cls}|{title}|{procId}"))
                        list.Add(new WindowInfo(title, cls, procId, !child.Current.IsOffscreen));
                }
            }
            catch { /* 只读探测失败视为无窗口 */ }

            // Win32 兜底：**托盘/最小化的窗口 UIA 看不到**，但它仍然存在——
            // 不补这一步，"微信在不在运行"会被误判成"未运行"，托盘里的窗口也永远没人去唤它。
            foreach (var w in WeChatWindowFinder.FindAll())
                if (seen.Add($"{w.ClassName}|{w.Title}|{w.ProcessId}"))
                    list.Add(new WindowInfo(w.Title, w.ClassName, w.ProcessId, w.Visible));

            return list;
        }, ct);

    public async Task<VisibleChatRead> ReadVisibleChatAsync(CancellationToken ct = default)
    {
        try
        {
            var win = await Task.Run(FindMainWindow, ct);
            if (win is null)
                return new VisibleChatRead { Success = false, Error = "未找到微信窗口" };
            if (win.Current.IsOffscreen)
                return new VisibleChatRead { Success = false, Error = "微信窗口已最小化/不可见" };

            var messages = await Task.Run(() => CollectTextMessages(win, ct), ct);

            // 微信 4.x 为 Qt 自绘，UIA 树只有 Pane、无文本节点 → 走 OCR 读屏兜底
            if (messages.Count == 0)
            {
                var hwnd = new IntPtr(win.Current.NativeWindowHandle);
                if (hwnd == IntPtr.Zero)
                    return new VisibleChatRead { Success = false, Error = "窗口句柄不可用" };

                var (ok, method, lines, error) = await OcrScreenReader.ReadAsync(hwnd, ct);
                if (!ok)
                    return new VisibleChatRead { Success = false, Error = $"UIA 与 OCR 均未读到内容：{error}" };

                messages = lines.Where(t => t.Length >= 2).Distinct().TakeLast(30).ToList();
                if (messages.Count == 0)
                    return new VisibleChatRead { Success = false, Error = "OCR 未识别到文本（窗口可能未打开会话）" };

                return new VisibleChatRead
                {
                    Success = true,
                    Messages = messages,
                    Text = string.Join("\n", messages),
                    Method = $"OCR/{method}",
                };
            }

            return new VisibleChatRead
            {
                Success = true,
                Messages = messages,
                Text = string.Join("\n", messages),
                Method = "UIA",
            };
        }
        catch (Exception ex)
        {
            return new VisibleChatRead { Success = false, Error = ex.Message };
        }
    }

    /// <summary>收集窗口内文本元素，取下部最近的若干条（聊天区通常在下方），保持出现顺序。</summary>
    private static List<string> CollectTextMessages(AutomationElement win, CancellationToken ct)
    {
        var texts = new List<string>();
        var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);
        foreach (AutomationElement e in win.FindAll(TreeScope.Descendants, cond))
        {
            if (ct.IsCancellationRequested) break;
            var name = (e.Current.Name ?? "").Trim();
            if (name.Length >= 2) texts.Add(name);
            if (texts.Count >= 2000) break; // 防御：元素过多时截断
        }

        return texts
            .Skip(Math.Max(0, texts.Count - 60))
            .Where(t => t.Length >= 2)
            .Distinct()
            .TakeLast(30)
            .ToList();
    }

    /// <summary>定位微信主窗（只枚举，不激活）。写操作桥复用此逻辑。</summary>
    internal static AutomationElement? FindMainWindow()
    {
        var root = AutomationElement.RootElement;
        AutomationElement? fallback = null;
        foreach (AutomationElement child in root.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            var cls = child.Current.ClassName ?? "";
            var title = child.Current.Name ?? "";
            var procId = child.Current.ProcessId;
            if (!IsWeChatWindow(cls, title, procId)) continue;
            if (ClassNames.Contains(cls)) return child;   // 老版微信主窗类名最可靠
            fallback ??= child;
        }
        return fallback;
    }

    /// <summary>类名 / 标题 / 进程名（Weixin、WeChat，兼容微信 4.x）三选一命中即视为微信窗口。</summary>
    private static bool IsWeChatWindow(string className, string title, int processId)
    {
        if (ClassNames.Contains(className)) return true;
        if (Titles.Contains(title)) return true;
        try
        {
            var name = Process.GetProcessById(processId).ProcessName;
            return name.Equals("Weixin", StringComparison.OrdinalIgnoreCase)
                || name.Equals("WeChat", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
