# Identify windows: with no args it describes the CURRENT FOREGROUND window; pass -Pid N to list that
# process's windows. Written for one recurring mystery: synthesized keys "get swallowed" by WeChat while
# clicks still work -- which happens when the foreground is held by some other (often stuck/hidden)
# window, so the keys never land where the app thinks they should.
param(
    [int]$ProcId = 0,          # -ProcId N lists that process's windows
    [IntPtr]$Hwnd = [IntPtr]::Zero,
    [switch]$AllVisible,
    [int]$PointX = -1,         # -PointX/-PointY: which window is really under that screen point
    [int]$PointY = -1
)

$ErrorActionPreference = "Stop"
Add-Type -Namespace WI -Name Native -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr h);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
[DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
public delegate bool EnumProc(IntPtr h, IntPtr p);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

// "Which window is actually under this point?" -- the direct answer to "my click did nothing":
// a transparent/topmost overlay (game overlay, on-screen helper) sits above WeChat and eats it.
public static string WindowAt(int x, int y) {
    POINT p; p.X = x; p.Y = y;
    IntPtr h = WindowFromPoint(p);
    IntPtr root = h == IntPtr.Zero ? IntPtr.Zero : GetAncestor(h, 2);   // GA_ROOT
    return "point=" + x + "," + y + " -> hit=" + h + " ; root=" + Describe(root);
}

public static string Describe(IntPtr h) {
    var t = new System.Text.StringBuilder(256); GetWindowTextW(h, t, 256);
    var c = new System.Text.StringBuilder(256); GetClassNameW(h, c, 256);
    uint pid; GetWindowThreadProcessId(h, out pid);
    RECT r; GetWindowRect(h, out r);
    string proc; try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { proc = "?"; }
    return string.Format("hwnd={0} pid={1}({2}) visible={3} hung={4} owner={5} rect={6},{7} {8}x{9} class='{10}' title='{11}'",
        h, pid, proc, IsWindowVisible(h), IsHungAppWindow(h), GetWindow(h, 4),
        r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, c, t);
}

public static System.Collections.Generic.List<IntPtr> WindowsOf(uint want, bool visibleOnly) {
    var found = new System.Collections.Generic.List<IntPtr>();
    EnumWindows((h, p) => {
        uint pid; GetWindowThreadProcessId(h, out pid);
        if (pid != want) return true;
        if (visibleOnly && !IsWindowVisible(h)) return true;
        found.Add(h);
        return true;
    }, IntPtr.Zero);
    return found;
}

public static System.Collections.Generic.List<IntPtr> AllVisible() {
    var found = new System.Collections.Generic.List<IntPtr>();
    EnumWindows((h, p) => {
        if (IsWindowVisible(h)) found.Add(h);
        return true;
    }, IntPtr.Zero);
    return found;
}
'@

[void][WI.Native]::SetProcessDPIAware()

if ($PointX -ge 0 -and $PointY -ge 0) {
    [WI.Native]::WindowAt($PointX, $PointY)
    "under-cursor is decided by WindowFromPoint (deepest window), root via GetAncestor(GA_ROOT)"
    exit 0
}

if ($Hwnd -ne [IntPtr]::Zero) {
    [WI.Native]::Describe($Hwnd)
    exit 0
}

if ($ProcId -gt 0) {
    foreach ($h in [WI.Native]::WindowsOf([uint32]$ProcId, $true)) { [WI.Native]::Describe($h) }
    exit 0
}

$fg = [WI.Native]::GetForegroundWindow()
"FOREGROUND " + [WI.Native]::Describe($fg)

if ($AllVisible) {
    "--- visible top-level windows ---"
    foreach ($h in [WI.Native]::AllVisible()) { [WI.Native]::Describe($h) }
}
