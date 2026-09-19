# Wait for the app's modal decision dialog ("Agent wants a decision") and pick one of its options.
# The dialog is found structurally, not by title/label text (CJK in a .ps1 would be mangled by PS 5.1):
#   visible + belongs to the app pid + not the main window + 200..1400 x 100..1000 px.
# The decision dialog measures 910x560 px (its WPF 520x320 DIP at 175% scaling) -- NOT 520x320.
#
# IMPORTANT: the write-confirmation dialog's focused/default option is "cancel" (the gate sets
# DefaultOption = 取消 on purpose: a stray Enter must never send a message). So SPACE/ENTER cancel it.
# To accept we must click the option button itself, which is why this script clicks a button by index.
param(
    [string]$Proc = "MemoryAssistant.App",
    [int]$MaxSeconds = 45,
    [int]$PollMs = 400,
    [int]$Index = 0,          # 0 = first option (确认执行), 1 = 以后都允许, ...
    [int]$FromEnd = 0,        # 1 = take the last option instead (取消)
    [switch]$ReportOnly
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -Namespace Ma3 -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
public delegate bool EnumProc(IntPtr h, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
[DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
[DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
[StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
[StructLayout(LayoutKind.Explicit)] public struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
[StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }

// SetForegroundWindow alone often FAILS when another app (WeChat, just brought up by the bridge)
// holds the foreground -- Windows refuses the request and the click then lands on WeChat, which is
// exactly how a rehearsal left the dialog unanswered. Attaching to the target's input thread is the
// documented workaround that makes the foreground switch actually happen.
public static bool ForceForeground(IntPtr h) {
    if (GetForegroundWindow() == h) return true;
    uint pid;
    uint tid = GetWindowThreadProcessId(h, out pid);   // no inline 'out uint' -- Add-Type's C# 5 compiler rejects it
    uint mine = GetCurrentThreadId();
    AttachThreadInput(mine, tid, true);
    ShowWindow(h, 9);            // SW_RESTORE
    bool ok = SetForegroundWindow(h);
    AttachThreadInput(mine, tid, false);
    System.Threading.Thread.Sleep(150);
    return GetForegroundWindow() == h;
}

public static void Click(int x, int y) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(150);
    INPUT[] a = new INPUT[2];
    a[0].type = 0; a[0].u.mi.dwFlags = 0x0002;
    a[1].type = 0; a[1].u.mi.dwFlags = 0x0004;
    SendInput(2, a, Marshal.SizeOf(typeof(INPUT)));
}

public static System.Collections.Generic.List<IntPtr> SmallTopLevel(uint want, IntPtr main) {
    var found = new System.Collections.Generic.List<IntPtr>();
    EnumWindows((h, p) => {
        uint pid; GetWindowThreadProcessId(h, out pid);
        if (pid != want) return true;
        if (!IsWindowVisible(h)) return true;
        if (h == main) return true;
        RECT r; if (!GetWindowRect(h, out r)) return true;
        int w = r.Right - r.Left, hh = r.Bottom - r.Top;
        if (w < 200 || hh < 100) return true;
        if (w > 1400 || hh > 1000) return true;
        found.Add(h);
        return true;
    }, IntPtr.Zero);
    return found;
}
'@

[void][Ma3.Native]::SetProcessDPIAware()

$p = Get-Process -Name $Proc -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { throw "$Proc not running" }
$main = $p.MainWindowHandle
$deadline = (Get-Date).AddSeconds($MaxSeconds)
$picks = @{}      # hwnd -> chosen option (options are enumerated once per dialog, clicks may retry)

while ((Get-Date) -lt $deadline) {
    foreach ($h in [Ma3.Native]::SmallTopLevel([uint32]$p.Id, $main)) {
        if (-not $picks.ContainsKey([int64]$h)) {
            $r = New-Object Ma3.Native+RECT
            [void][Ma3.Native]::GetWindowRect($h, [ref]$r)
            "DIALOG hwnd=$h size=$($r.Right - $r.Left)x$($r.Bottom - $r.Top)"

            # Option buttons: any clickable-looking element inside the dialog, sorted left to right.
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
            $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            $opts = @()
            foreach ($e in $all) {
                $ct = $e.Current.ControlType.ProgrammaticName
                if ($ct -notin @('ControlType.Button', 'ControlType.DataItem', 'ControlType.ListItem')) { continue }
                $b = $e.Current.BoundingRectangle
                if ($b.Width -lt 40 -or $b.Height -lt 20) { continue }
                $cx = $b.X + $b.Width / 2; $cy = $b.Y + $b.Height / 2
                if ($cx -lt $r.Left -or $cx -gt $r.Right -or $cy -lt $r.Top -or $cy -gt $r.Bottom) { continue }
                $opts += [pscustomobject]@{ X = [int]$cx; Y = [int]$cy; Name = $e.Current.Name; Kind = $ct }
            }
            $opts = $opts | Sort-Object X, Y
            foreach ($o in $opts) { "  option '$($o.Name)' at $($o.X),$($o.Y) ($($o.Kind))" }
            if ($opts.Count -eq 0) { "  (no option buttons seen)"; continue }

            $picks[[int64]$h] = if ($FromEnd -eq 1) { $opts[$opts.Count - 1] }
                                elseif ($Index -lt $opts.Count) { $opts[$Index] }
                                else { $opts[$opts.Count - 1] }
            if ($ReportOnly) { "REPORT-ONLY would click '$($picks[[int64]$h].Name)'"; exit 0 }
        }

        $pick = $picks[[int64]$h]
        [void][Ma3.Native]::ForceForeground($h)
        Start-Sleep -Milliseconds 200
        [Ma3.Native]::Click($pick.X, $pick.Y)
        Start-Sleep -Milliseconds 700
        if (-not [Ma3.Native]::IsWindowVisible($h)) { "PICKED '$($pick.Name)'"; exit 0 }
        "STILL-UP hwnd=$h (click missed? retrying)"
    }
    Start-Sleep -Milliseconds $PollMs
}
"NO-DIALOG within $MaxSeconds s"
exit 1
