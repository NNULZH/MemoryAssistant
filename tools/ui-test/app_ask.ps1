# Ask the MemoryAssistant window a question (drive it like a human):
#   1) UIA -> AutomationId "Input" (the chat input box), click it
#   2) type the text from -TextFile (UTF-8; PS 5.1 would mangle CJK if inlined here)
#   3) UIA -> the button at the window's bottom-right corner (the Send button) and invoke it
# ASCII only on purpose: PowerShell 5.1 reads .ps1 as ANSI and mangles CJK literals.
param(
    [string]$Text = "",
    [string]$TextFile = "",
    [string]$Proc = "MemoryAssistant.App",
    [switch]$NoClick,          # only type + send (input already focused)
    [switch]$DryRun            # print what would be touched, do not type/send
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -Namespace Ma2 -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
[DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
[StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
[StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
[StructLayout(LayoutKind.Explicit)] public struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
[StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }

public static void Click(int x, int y) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(120);
    INPUT[] a = new INPUT[2];
    a[0].type = 0; a[0].u.mi.dwFlags = 0x0002;
    a[1].type = 0; a[1].u.mi.dwFlags = 0x0004;
    SendInput(2, a, Marshal.SizeOf(typeof(INPUT)));
}

public static int TypeText(string s) {
    System.Collections.Generic.List<INPUT> list = new System.Collections.Generic.List<INPUT>();
    foreach (char c in s) {
        INPUT d = new INPUT(); d.type = 1; d.u.ki.wScan = c; d.u.ki.dwFlags = 0x0004; list.Add(d);
        INPUT u = new INPUT(); u.type = 1; u.u.ki.wScan = c; u.u.ki.dwFlags = 0x0006; list.Add(u);
    }
    if (list.Count == 0) return 0;
    INPUT[] arr = list.ToArray();
    return (int)SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(INPUT)));
}
'@ -ReferencedAssemblies 'System.Drawing'

[void][Ma2.Native]::SetProcessDPIAware()

$payload = $Text
if ($TextFile) { $payload = [IO.File]::ReadAllText($TextFile, [Text.Encoding]::UTF8).Trim() }
if (-not $payload) { throw "nothing to send (pass -Text or -TextFile)" }

$p = Get-Process -Name $Proc -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { throw "$Proc main window not found" }
$hwnd = $p.MainWindowHandle

[void][Ma2.Native]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 400

$rc = New-Object Ma2.Native+RECT
[void][Ma2.Native]::GetWindowRect($hwnd, [ref]$rc)
$w = $rc.Right - $rc.Left; $h = $rc.Bottom - $rc.Top
"WINDOW x=$($rc.Left) y=$($rc.Top) w=$w h=$h"

$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)

if (-not $NoClick) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "Input")
    $input = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $input) { throw "chat input (AutomationId=Input) not found" }
    $ir = $input.Current.BoundingRectangle
    $ix = [int]($ir.X + $ir.Width / 2); $iy = [int]($ir.Y + $ir.Height / 2)
    "INPUT x=$ix y=$iy w=$($ir.Width) h=$($ir.Height)"
    if ($DryRun) { "DRYRUN: would click input and send '$payload'"; exit 0 }
    [Ma2.Native]::Click($ix, $iy)
    Start-Sleep -Milliseconds 350
} elseif ($DryRun) { "DRYRUN: would type '$payload'"; exit 0 }

$sent = [Ma2.Native]::TypeText($payload)
"TYPED chars=$($payload.Length) events=$sent"
Start-Sleep -Milliseconds 500

# Send button = the button nearest the window's bottom-right corner (no CJK matching needed).
$btnCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)
$buttons = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
$best = $null; $bestScore = -1.0
foreach ($b in $buttons) {
    $r = $b.Current.BoundingRectangle
    if ($r.Width -lt 40 -or $r.Height -lt 20) { continue }
    if (-not $b.Current.IsEnabled) { continue }
    $cx = ($r.X + $r.Width / 2 - $rc.Left) / $w
    $cy = ($r.Y + $r.Height / 2 - $rc.Top) / $h
    if ($cx -lt 0.7 -or $cy -lt 0.7) { continue }
    $score = $cx + $cy
    if ($score -gt $bestScore) { $bestScore = $score; $best = $b }
}
if (-not $best) { throw "send button not found near bottom-right" }
$br = $best.Current.BoundingRectangle
"SENDBTN x=$([int]($br.X + $br.Width/2)) y=$([int]($br.Y + $br.Height/2)) name='$($best.Current.Name)'"

try {
    $inv = $best.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $inv.Invoke()
    "INVOKED"
} catch {
    [Ma2.Native]::Click([int]($br.X + $br.Width / 2), [int]($br.Y + $br.Height / 2))
    "CLICKED (no InvokePattern)"
}
"SENT"
