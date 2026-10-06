param(
    [string]$Root,
    [int]$TimeoutSec = 540,
    [int]$AppearSec = 90,
    [int]$QuietSec = 8,
    [string]$Shot = "",
    [string]$TitleLike = "",
    [switch]$Close
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[Win32]::SetProcessDPIAware() | Out-Null

$started = Get-Date
$log = Join-Path $Root "instance\logs\latest.log"
$launcherLog = Join-Path $Root "launcher.log"
$gameStarted = 'PID (\d+)$'

function Elapsed {
    "{0,4:N0}s" -f ((Get-Date) - $started).TotalSeconds
}

function Find-Game {
    if (-not (Test-Path $launcherLog)) { return }
    $line = Get-Content $launcherLog -Encoding UTF8 | Where-Object { $_ -match $gameStarted } | Select-Object -Last 1
    if ($line -match $gameStarted) { Get-Process -Id ([int]$Matches[1]) -ErrorAction SilentlyContinue }
}

function Save-Shot($window, $path) {
    $rect = New-Object 'Win32+RECT'
    [Win32]::GetWindowRect($window, [ref]$rect) | Out-Null
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) { "$(Elapsed) window is minimized, no screenshot"; return }
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    $drawn = [Win32]::PrintWindow($window, $dc, 2)
    $graphics.ReleaseHdc($dc)
    $graphics.Dispose()
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    "$(Elapsed) screenshot ${width}x$height drawn=$drawn -> $path"
}

function Report($game) {
    "$(Elapsed) window title: $($game.MainWindowTitle)"
    if (-not (Test-Path $log)) { return }
    $lines = Get-Content $log
    $bad = @($lines | Where-Object { $_ -match '/(ERROR|FATAL)\]' })
    "$(Elapsed) log: $($lines.Count) lines, errors: $($bad.Count)"
    $bad | ForEach-Object { $text = $_ -replace '^\[[^\]]*\] ', ''; $text.Substring(0, [Math]::Min(170, $text.Length)) } |
        Group-Object | Sort-Object Count -Descending | Select-Object -First 14 | ForEach-Object { "      x$($_.Count) $($_.Name)" }
}

$game = $null
$reached = $false
while (((Get-Date) - $started).TotalSeconds -lt $TimeoutSec) {
    if (-not $game) {
        $game = Find-Game
        if (-not $game -and ((Get-Date) - $started).TotalSeconds -gt $AppearSec) { break }
        if (-not $game) { Start-Sleep -Milliseconds 500; continue }
        "$(Elapsed) game process $($game.Id)"
    }
    $game.Refresh()
    if ($game.HasExited) { "$(Elapsed) GAME EXITED, code $($game.ExitCode)"; break }
    if ($game.MainWindowHandle -ne 0 -and (Test-Path $log)) {
        $quiet = ((Get-Date) - (Get-Item $log).LastWriteTime).TotalSeconds
        $titleOk = -not $TitleLike -or $game.MainWindowTitle -like $TitleLike
        if ($titleOk -and $quiet -ge $QuietSec -and (Select-String -Path $log -Pattern '-atlas' -Quiet)) {
            $reached = $true
            "$(Elapsed) loaded: log quiet for $([int]$quiet) s"
            break
        }
    }
    Start-Sleep -Seconds 1
}

if (-not $game) { "$(Elapsed) NO GAME PROCESS for $Root"; exit 2 }
if ($game.HasExited) {
    if (Test-Path $log) { Get-Content $log -Tail 25 | ForEach-Object { "      " + $_.Substring(0, [Math]::Min(200, $_.Length)) } }
    exit 3
}
if (-not $reached) { "$(Elapsed) TIMEOUT, game still running" }
Report $game
if ($Shot) { Save-Shot $game.MainWindowHandle $Shot }
if ($Close) {
    $game.CloseMainWindow() | Out-Null
    if ($game.WaitForExit(40000)) { "$(Elapsed) game closed, code $($game.ExitCode)" }
    else { $game.Kill(); "$(Elapsed) game did not close in 40 s, killed" }
}
if ($reached) { exit 0 } else { exit 1 }
