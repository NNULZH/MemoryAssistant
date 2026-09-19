using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MemoryAssistant.App.Integrations;

/// <summary>
/// 微信主窗口查找（Win32 层，两个桥共用；规则只写一遍）。
///
/// 为什么不能只用 UIAutomation：**窗口缩到托盘 / 被隐藏时，UIA 的 RootElement 里根本看不到它**，
/// 于是"发现 0 个微信窗口"——但窗口其实还在，只是不可见。
/// 结果就是：<c>ShowWindow(SW_RESTORE)</c> 那段"从托盘唤出"的代码永远执行不到（永远走不到那一步）。
/// <c>EnumWindows</c> 会枚举**所有顶层窗口（含隐藏/最小化）**，所以托盘里的微信也能被找到、被还原。
/// </summary>
internal static class WeChatWindowFinder
{
    /// <summary>找到的一个微信主窗口。</summary>
    internal readonly record struct Found(IntPtr Hwnd, string Title, string ClassName, int ProcessId, bool Visible)
    {
        public override string ToString()
            => $"{(Title.Length > 0 ? Title : "(无标题)")} ({ClassName}, pid={ProcessId}){(Visible ? "" : " · 已最小化/隐藏")}";
    }

    /// <summary>枚举所有微信主窗口（**含最小化/托盘隐藏的**）。</summary>
    public static IReadOnlyList<Found> FindAll()
    {
        var list = new List<Found>();
        try
        {
            EnumWindows((h, _) =>
            {
                if (!IsMainWindowCandidate(h)) return true;
                GetWindowThreadProcessId(h, out var pid);
                if (!IsWeChatProcess((int)pid)) return true;

                var tb = new StringBuilder(256);
                GetWindowText(h, tb, 256);
                var title = tb.ToString();

                var cb = new StringBuilder(256);
                GetClassName(h, cb, 256);
                var cls = cb.ToString();

                list.Add(new Found(h, title, cls, (int)pid, IsWindowVisible(h)));
                return true;
            }, IntPtr.Zero);
        }
        catch { /* 枚举失败按"没找到"处理，由调用方给提示 */ }
        return list;
    }

    /// <summary>
    /// 取一个可用句柄：优先可见的（当前正在用的那个），没有就取隐藏/最小化的（供调用方还原）。
    /// </summary>
    public static IntPtr FindHandle()
    {
        var all = FindAll();
        foreach (var w in all)
            if (w.Visible) return w.Hwnd;
        return all.Count > 0 ? all[0].Hwnd : IntPtr.Zero;
    }

    /// <summary>类名/标题是否符合微信主窗口（4.x 是 Qt 窗口 <c>Qt51514QWindowIcon</c>，老版类名固定）。</summary>
    private static bool IsMainWindowCandidate(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, 256);
        var cls = sb.ToString();

        if (cls is "WeChatMainWndForPC" or "WeChatLoginWndForPC") return true;
        if (!cls.EndsWith("QWindowIcon", StringComparison.Ordinal)) return false;

        // 4.x 主窗标题为「微信」；无标题的 Qt 辅助窗要排掉（否则会把一堆内部窗口也算进来）
        var tb = new StringBuilder(256);
        GetWindowText(h, tb, 256);
        return tb.ToString() is "微信" or "WeChat" or "Weixin";
    }

    private static bool IsWeChatProcess(int pid)
    {
        try
        {
            var name = Process.GetProcessById(pid).ProcessName;
            return name.Equals("Weixin", StringComparison.OrdinalIgnoreCase)
                || name.Equals("WeChat", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
}
