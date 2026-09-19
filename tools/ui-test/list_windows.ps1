# List the top-level windows of a process (class / title / visibility / rect).
# ASCII only: PowerShell 5.1 reads .ps1 as ANSI and mangles CJK.
param([string]$Proc = "Weixin")
$ErrorActionPreference = "Stop"
Add-Type -Namespace Ma -Name L -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
[DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
public delegate bool EnumProc(IntPtr h, IntPtr p);
'@
[void][Ma.L]::SetProcessDPIAware()

$pids = @(Get-Process -Name $Proc -ErrorAction SilentlyContinue | ForEach-Object { [uint32]$_.Id })
"PIDS: " + ($pids -join ',')

$cb = [Ma.L+EnumProc] {
    param($w, $p)
    $pid2 = [uint32]0
    [void][Ma.L]::GetWindowThreadProcessId($w, [ref]$pid2)
    $cls = New-Object System.Text.StringBuilder 256
    [void][Ma.L]::GetClassName($w, $cls, 256)
    $ttl = New-Object System.Text.StringBuilder 256
    [void][Ma.L]::GetWindowText($w, $ttl, 256)
    $isTarget = $pids -contains $pid2
    $hasTitle = $ttl.Length -gt 0
    if (-not $isTarget -and -not $hasTitle) { return $true }
    $r = New-Object Ma.L+RECT
    [void][Ma.L]::GetWindowRect($w, [ref]$r)
    $vis = [Ma.L]::IsWindowVisible($w)
    $ico = [Ma.L]::IsIconic($w)
    "TARGET=$isTarget pid=$pid2 HWND=$w class=[$($cls.ToString())] title=[$($ttl.ToString())] visible=$vis iconic=$ico rect=$($r.L),$($r.T) $($r.R - $r.L)x$($r.B - $r.T)"
    return $true
}
[void][Ma.L]::EnumWindows($cb, [IntPtr]::Zero)
