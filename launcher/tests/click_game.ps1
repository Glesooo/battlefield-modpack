param([string]$Root, [int]$X, [int]$Y)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Mouse {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr window, ref POINT point);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[Mouse]::SetProcessDPIAware() | Out-Null

$rootWindow = 2
$leftDown = 2
$leftUp = 4
$line = Get-Content (Join-Path $Root "launcher.log") -Encoding UTF8 | Where-Object { $_ -match 'PID (\d+)$' } | Select-Object -Last 1
if (-not ($line -match 'PID (\d+)$')) { "no game PID in launcher.log"; exit 2 }
$game = Get-Process -Id ([int]$Matches[1]) -ErrorAction SilentlyContinue
if (-not $game -or $game.MainWindowHandle -eq 0) { "game window not found"; exit 2 }

$window = $game.MainWindowHandle
[Mouse]::SetForegroundWindow($window) | Out-Null
Start-Sleep -Milliseconds 400
$point = New-Object 'Mouse+POINT'
$point.X = $X
$point.Y = $Y
[Mouse]::ClientToScreen($window, [ref]$point) | Out-Null
if ([Mouse]::GetAncestor([Mouse]::WindowFromPoint($point), $rootWindow) -ne $window) {
    "game window is not on top at $X,$Y - no click"
    exit 1
}
[Mouse]::SetCursorPos($point.X, $point.Y) | Out-Null
Start-Sleep -Milliseconds 200
[Mouse]::mouse_event($leftDown, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 60
[Mouse]::mouse_event($leftUp, 0, 0, 0, [UIntPtr]::Zero)
"clicked at $X,$Y"
