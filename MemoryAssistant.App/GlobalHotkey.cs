using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MemoryAssistant.App;

/// <summary>
/// 全局热键：**不依赖主窗口**，所以"关掉/收起窗口、服务还在后台跑任务"时依然有效——
/// 这正是它存在的理由（任务万一跑飞，用户不该为了按一个按钮先把窗口找回来）。
///
/// 做法：建一个 0×0 的消息窗口（HwndSource）专门收 WM_HOTKEY。
/// 主窗口收起/关闭后这个窗口仍在（它属于应用进程，不属于 MainWindow），热键不会失效。
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    /// <summary>按住不放时只触发一次（否则会连着触发好几次，把日志刷爆）。</summary>
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly int _id;

    /// <summary>注册是否成功（被别的软件占用时会失败——那就自动换下一个候选键位）。</summary>
    public bool Registered { get; }

    /// <summary>实际生效的组合键（人话，如 "Ctrl+Alt+S"）。</summary>
    public string Description { get; }

    /// <summary>这个键干什么用（进日志，让人知道有哪些后路）。</summary>
    public string Purpose { get; }

    private GlobalHotkey(int id, string description, string purpose, uint modifiers, uint vk, Action onPressed)
    {
        _id = id;
        Description = description;
        Purpose = purpose;
        var p = new HwndSourceParameters("MemoryAssistantHotkeyWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,                 // 不可见、不进任务栏，只收消息
            ParentWindow = new IntPtr(-3),   // HWND_MESSAGE：消息窗口
        };
        _source = new HwndSource(p);
        _source.AddHook((IntPtr _, int msg, IntPtr wParam, IntPtr _, ref bool handled) =>
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
            {
                handled = true;
                onPressed();
            }
            return IntPtr.Zero;
        });
        Registered = RegisterHotKey(_source.Handle, _id, modifiers | MOD_NOREPEAT, vk);
    }

    /// <summary>
    /// 注册一个动作的全局热键：**按候选顺序试**，第一个注册成功的就是生效键。
    ///
    /// 为什么要退让：热键是抢独占的，Ctrl+Alt+M 之类的组合很可能已被别的软件占着
    /// （实测就有一个注册不上）。静默留一个按不动的键最糟——用户以为有后路，其实没有；
    /// 所以这里换一个可用的，并把**实际生效的键**写进日志与界面提示。
    /// </summary>
    public static GlobalHotkey? Bind(int id, string purpose, Action onPressed,
        params (uint Vk, string Name)[] candidates)
    {
        foreach (var (vk, name) in candidates)
        {
            var hotkey = new GlobalHotkey(id, "Ctrl+Alt+" + name, purpose, MOD_CONTROL | MOD_ALT, vk, onPressed);
            if (hotkey.Registered) return hotkey;
            hotkey.Dispose();     // 没抢到就把消息窗口拆掉，别留垃圾窗口
        }
        return null;
    }

    public void Dispose()
    {
        if (Registered) UnregisterHotKey(_source.Handle, _id);
        _source.Dispose();
    }
}
