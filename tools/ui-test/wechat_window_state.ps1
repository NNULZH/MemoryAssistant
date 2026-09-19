# Put the WeChat main window into one of three states, to exercise the action bridge:
#   normal = restored on screen | min = minimized to taskbar | tray = hidden (tray)
# ASCII only: PowerShell 5.1 reads .ps1 as ANSI and mangles CJK.
param(
    [string]$State = 'normal',
    [string]$Proc = 'Weixin'
)
$ErrorActionPreference = 'Stop'

Add-Type -Namespace MaW -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
public delegate bool EnumProc(IntPtr h, IntPtr p);
'@
[void][MaW.U]::SetProcessDPIAware()

$SW_HIDE = 0
$SW_MINIMIZE = 6
$SW_RESTORE = 9

$all = @(Get-Process -Name $Proc -ErrorAction SilentlyContinue)
if ($all.Count -eq 0) { throw "process not running: $Proc" }
$pids = @($all | ForEach-Object { [uint32]$_.Id })

# MainWindowHandle is 0 while the window is hidden to the tray, so fall back to enumeration.
$script:found = [IntPtr]::Zero
$visible = $all | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } | Select-Object -First 1
if ($visible) {
    $script:found = $visible.MainWindowHandle
} else {
    $cb = [MaW.U+EnumProc] {
        param($h, $p)
        $owner = [uint32]0
        [void][MaW.U]::GetWindowThreadProcessId($h, [ref]$owner)
        if ($pids -notcontains $owner) { return $true }
        $cls = New-Object System.Text.StringBuilder 256
        [void][MaW.U]::GetClassName($h, $cls, 256)
        if ($cls.ToString() -notlike '*QWindow*') { return $true }
        $script:found = $h
        return $false
    }
    [void][MaW.U]::EnumWindows($cb, [IntPtr]::Zero)
}
if ($script:found -eq [IntPtr]::Zero) { throw "main window not found: $Proc" }
$h = $script:found

switch ($State) {
    'min'  { [void][MaW.U]::ShowWindow($h, $SW_MINIMIZE) }
    'tray' { [void][MaW.U]::ShowWindow($h, $SW_HIDE) }
    default {
        [void][MaW.U]::ShowWindow($h, $SW_RESTORE)
        Start-Sleep -Milliseconds 300
        [void][MaW.U]::MoveWindow($h, 907, 240, 1620, 950, $true)
        [void][MaW.U]::SetForegroundWindow($h)
    }
}
Start-Sleep -Milliseconds 800
"state=$State hwnd=$h iconic=$([MaW.U]::IsIconic($h)) visible=$([MaW.U]::IsWindowVisible($h))"
