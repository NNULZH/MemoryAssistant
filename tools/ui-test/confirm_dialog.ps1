# Poll the app window for the WeChat write-confirmation dialog (OCR), optionally click its confirm button.
# ASCII only: PowerShell 5.1 reads .ps1 as ANSI and mangles CJK. The search pattern comes from a UTF-8 file.
param(
    [int]$MaxSeconds = 60,
    [int]$PollSeconds = 6,
    [string]$PatternFile = "",
    [switch]$Confirm,
    [int]$BtnX = 1389,
    [int]$BtnY = 782
)
$ErrorActionPreference = "Stop"
if (-not $PatternFile) { throw "PatternFile required" }
$pattern = ([IO.File]::ReadAllText($PatternFile, [Text.Encoding]::UTF8)).Trim()
# 用同目录下的 OCR 驱动脚本（不再依赖 %TEMP% 里的那份）
$ocr = Join-Path $PSScriptRoot 'ocr_drive.ps1'

for ($t = 0; $t -lt $MaxSeconds; $t += $PollSeconds) {
    Start-Sleep -Seconds $PollSeconds
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $ocr -Action ocr -Mode screen 2>&1
    $flat = ($out -join "`n") -replace '[\s\u3000]', ''
    $found = $flat.Contains($pattern)
    "poll t=$t found=$found"
    if ($found) {
        if ($Confirm) {
            & powershell -NoProfile -ExecutionPolicy Bypass -File $ocr -Action click -X $BtnX -Y $BtnY 2>&1
            "CONFIRMED"
        }
        exit 0
    }
}
"NO-DIALOG within $MaxSeconds s"
exit 1
