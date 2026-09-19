# GUI driver: screenshot + Windows.Media.Ocr + simulated mouse/keyboard.
# Purpose: drive the MemoryAssistant window like a human (find input box / send button,
# click, type, click send) and OCR-read back the window to verify the tool-call panel.
# NOTE: this file must stay pure ASCII -- PowerShell 5.1 reads .ps1 as ANSI and mangles CJK.
param(
    [string]$Action = "ocr",     # ocr | click | type | clicktype | key
    [int]$X = 0,
    [int]$Y = 0,
    [string]$Text = "",
    [string]$TextFile = "",
    [string]$Key = "",
    [string]$Shot = "",
    [switch]$Verify,
    [string]$Mode = "print",
    [string]$Proc = "MemoryAssistant.App",
    [string]$OcrFile = "",     # OCR an existing PNG instead of shooting the window (coords are then image-relative)
    [int]$Repeat = 1,          # -Action key: send the key N times (one activation, so fewer dropped keys)
    [switch]$Force             # really steal the foreground (AttachThreadInput) before typing/clicking
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Runtime.WindowsRuntime

Add-Type -Namespace Ma -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
[StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
[StructLayout(LayoutKind.Explicit)] public struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
[StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }
[DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
[DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }

// SetForegroundWindow from a background process (a script launched by an IDE/CI shell) is usually
// REFUSED by Windows: the window stays behind, the synthesized keys land in whatever window really
// has the focus, and the app then reports "the input channel swallowed my text".
// Attaching to the target's input thread is the documented workaround that makes it actually stick.
public static bool ForceForeground(IntPtr h) {
    if (GetForegroundWindow() == h) return true;
    uint pid;
    uint tid = GetWindowThreadProcessId(h, out pid);
    uint mine = GetCurrentThreadId();
    AttachThreadInput(mine, tid, true);
    ShowWindow(h, 9);
    bool ok = SetForegroundWindow(h);
    AttachThreadInput(mine, tid, false);
    System.Threading.Thread.Sleep(200);
    return GetForegroundWindow() == h;
}

// PrintWindow grabs the window's own rendering, so it still works when another
// window (e.g. WeChat being driven) covers it -- CopyFromScreen would capture WeChat instead.
public static bool PrintWindowToBitmap(IntPtr hwnd, System.Drawing.Graphics g) {
    IntPtr hdc = g.GetHdc();
    bool ok = PrintWindow(hwnd, hdc, 2);   // PW_RENDERFULLCONTENT
    g.ReleaseHdc(hdc);
    return ok;
}

public static string Click(int x, int y) {
    bool moved = SetCursorPos(x, y);
    System.Threading.Thread.Sleep(120);
    POINT p; GetCursorPos(out p);
    INPUT[] a = new INPUT[2];
    a[0].type = 0; a[0].u.mi.dwFlags = 0x0002;
    a[1].type = 0; a[1].u.mi.dwFlags = 0x0004;
    uint sent = SendInput(2, a, Marshal.SizeOf(typeof(INPUT)));
    return "moved=" + moved + " cursor=" + p.X + "," + p.Y + " want=" + x + "," + y + " sent=" + sent;
}

public static string TypeText(string s) {
    System.Collections.Generic.List<INPUT> list = new System.Collections.Generic.List<INPUT>();
    foreach (char c in s) {
        INPUT d = new INPUT(); d.type = 1; d.u.ki.wScan = c; d.u.ki.dwFlags = 0x0004; list.Add(d);
        INPUT u = new INPUT(); u.type = 1; u.u.ki.wScan = c; u.u.ki.dwFlags = 0x0006; list.Add(u);
    }
    if (list.Count == 0) return "empty";
    INPUT[] arr = list.ToArray();
    uint sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(INPUT)));
    return "typedChars=" + s.Length + " sent=" + sent + " want=" + arr.Length + " inputSize=" + Marshal.SizeOf(typeof(INPUT));
}

public static void SendVk(ushort vk) {
    INPUT[] a = new INPUT[2];
    a[0].type = 1; a[0].u.ki.wVk = vk;
    a[1].type = 1; a[1].u.ki.wVk = vk; a[1].u.ki.dwFlags = 0x0002;
    SendInput(2, a, Marshal.SizeOf(typeof(INPUT)));
}
'@ -ReferencedAssemblies 'System.Drawing'

[void][Ma.Native]::SetProcessDPIAware()

$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime]

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    })[0]

function Await($op, $type) {
    $m = $asTaskGeneric.MakeGenericMethod($type)
    $t = $m.Invoke($null, @($op))
    $t.Wait(-1) | Out-Null
    $t.Result
}

function Get-AppWindow {
    $p = Get-Process -Name $Proc -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $p) { throw "$Proc main window not found" }
    return $p.MainWindowHandle
}

function Get-WindowRectPx([IntPtr]$h) {
    $r = New-Object Ma.Native+RECT
    [void][Ma.Native]::GetWindowRect($h, [ref]$r)
    return @{ X = $r.Left; Y = $r.Top; W = $r.Right - $r.Left; H = $r.Bottom - $r.Top }
}

function Save-WindowShot([IntPtr]$h, [string]$path, [string]$mode = 'print') {
    $rc = Get-WindowRectPx $h
    $bmp = New-Object System.Drawing.Bitmap($rc.W, $rc.H)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $ok = $false
    if ($mode -eq 'print') { $ok = [Ma.Native]::PrintWindowToBitmap($h, $g) }
    if (-not $ok) {
        # PrintWindow only paints THIS window; a modal dialog is its own window and would be missed.
        # mode=screen captures the desktop region instead (catches dialogs, but also whatever covers it).
        $g.CopyFromScreen($rc.X, $rc.Y, 0, 0, (New-Object System.Drawing.Size($rc.W, $rc.H)))
    }
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $rc
}

function Invoke-Ocr([string]$path) {
    $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
    if (-not $engine) { throw "OcrEngine unavailable (no OCR language pack)" }
    $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($path)) ([Windows.Storage.StorageFile])
    $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
    $res = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
    $out = @()
    foreach ($line in $res.Lines) {
        $minX = [int]::MaxValue; $minY = [int]::MaxValue; $maxX = 0; $maxY = 0
        foreach ($w in $line.Words) {
            $r = $w.BoundingRect
            if ($r.X -lt $minX) { $minX = [int]$r.X }
            if ($r.Y -lt $minY) { $minY = [int]$r.Y }
            if (($r.X + $r.Width) -gt $maxX) { $maxX = [int]($r.X + $r.Width) }
            if (($r.Y + $r.Height) -gt $maxY) { $maxY = [int]($r.Y + $r.Height) }
        }
        $out += [pscustomobject]@{
            X = $minX; Y = $minY; W = $maxX - $minX; H = $maxY - $minY
            CX = [int](($minX + $maxX) / 2); CY = [int](($minY + $maxY) / 2)
            Text = $line.Text
        }
    }
    return $out
}

# Standalone mode: OCR an already-saved image (e.g. a crop) and print its line boxes.
if ($OcrFile) {
    $lines = Invoke-Ocr $OcrFile
    "OCR-FILE $OcrFile"
    foreach ($l in $lines) { "LINE $($l.X)`t$($l.Y)`t$($l.W)`t$($l.H)`t$($l.Text)" }
    exit 0
}

$h = Get-AppWindow
[void][Ma.Native]::ShowWindow($h, 9)
if ($Force) {
    "FOREGROUND-STEAL ok=" + [Ma.Native]::ForceForeground($h)
} else {
    [void][Ma.Native]::SetForegroundWindow($h)
}
Start-Sleep -Milliseconds 500
$rc = Get-WindowRectPx $h

$payload = $Text
if ($TextFile) { $payload = [IO.File]::ReadAllText($TextFile, [Text.Encoding]::UTF8).Trim() }

# Do the action FIRST, then (optional) screenshot+OCR -- those take 1-2s and would drop the focus.
switch ($Action) {
    'click' {
        "CLICK " + [Ma.Native]::Click($rc.X + $X, $rc.Y + $Y)
    }
    'type' {
        "TYPE " + [Ma.Native]::TypeText($payload)
    }
    'key' {
        $map = @{ 'enter' = 0x0D; 'esc' = 0x1B; 'tab' = 0x09; 'back' = 0x08; 'end' = 0x23; 'home' = 0x24; 'del' = 0x2E }
        # [ushort] is not a valid PS 5.1 type accelerator (use [uint16]); the old line threw
        # "Unable to find type [ushort]" and made -Action key completely unusable.
        $n = if ($Repeat -lt 1) { 1 } else { $Repeat }
        for ($i = 0; $i -lt $n; $i++) {
            [Ma.Native]::SendVk([uint16]$map[$Key.ToLower()])
            Start-Sleep -Milliseconds 120
        }
        "KEY $Key x$n"
    }
    'clicktype' {
        "CLICK " + [Ma.Native]::Click($rc.X + $X, $rc.Y + $Y)
        Start-Sleep -Milliseconds 350
        "TYPE " + [Ma.Native]::TypeText($payload)
    }
    'ocr' { }
    default { throw "unknown Action: $Action" }
}

$fg = [Ma.Native]::GetForegroundWindow()
"FOREGROUND match=$($fg -eq $h) target=$h actual=$fg"

if ($Action -eq 'ocr' -or $Verify) {
    $shot = if ($Shot) { $Shot } else { Join-Path $env:TEMP 'ma_ocr_shot.png' }
    [void](Save-WindowShot $h $shot $Mode)
    $lines = Invoke-Ocr $shot
    "WINDOW x=$($rc.X) y=$($rc.Y) w=$($rc.W) h=$($rc.H)"
    foreach ($l in $lines) { "LINE $($l.X)`t$($l.Y)`t$($l.W)`t$($l.H)`t$($l.Text)" }
}
