param($exe, $root, $source, $empty, $out, $shot)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
}
"@
$auto = [System.Windows.Automation.AutomationElement]
$scope = [System.Windows.Automation.TreeScope]::Descendants
$report = @{}

function Wait-Until($condition, $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds 100
    }
    $false
}

function Open-Launcher($from) {
    $process = Start-Process $exe -ArgumentList "--dir `"$root`" --source `"$from`"" -PassThru
    Wait-Until { $process.Refresh(); $process.MainWindowHandle -ne 0 } 20 | Out-Null
    $process
}

function Find($process, $property, $value) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($property, $value)
    $auto::FromHandle($process.MainWindowHandle).FindFirst($scope, $condition)
}

function Element($process, $id) {
    Find $process $auto::AutomationIdProperty $id
}

function Click($element) {
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Busy($process) {
    -not (Element $process "PlayButton").Current.IsEnabled
}

function Status($process) {
    (Element $process "StageText").Current.Name
}

function Save-Shot($process, $path) {
    $rect = New-Object 'Win32+RECT'
    [Win32]::GetWindowRect($process.MainWindowHandle, [ref]$rect) | Out-Null
    $bitmap = New-Object System.Drawing.Bitmap(($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    [Win32]::PrintWindow($process.MainWindowHandle, $dc, 2) | Out-Null
    $graphics.ReleaseHdc($dc)
    $graphics.Dispose()
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

$app = Open-Launcher $source
$report.ready = Status $app

Click (Element $app "SettingsButton")
Wait-Until { (Element $app "SettingsSave") -ne $null } 5 | Out-Null
$java = (Element $app "JavaBox").GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$memory = (Element $app "MemorySlider").GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
$full = (Element $app "FullScreenBox").GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
$java.SetValue("bad")
Click (Element $app "SettingsSave")
Wait-Until { (Element $app "SettingsError").Current.Name -ne "" } 5 | Out-Null
$report.settingsError = (Element $app "SettingsError").Current.Name
$memory.SetValue(3500)
$full.Toggle()
Click (Element $app "SettingsDefaults")
$report.defaultsShown = Wait-Until { $memory.Current.Value -eq 4000 -and $java.Current.Value -eq "" -and $full.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off } 5
$memory.SetValue(3500)
$full.Toggle()
$java.SetValue("-Dmace.test=1")
Start-Sleep -Milliseconds 400
if ($shot) { Save-Shot $app $shot }
Click (Element $app "SettingsSave")
$report.settingsClosed = Wait-Until { (Element $app "SettingsSave") -eq $null } 5

Click (Element $app "VerifyButton")
$report.busy = Wait-Until { Busy $app } 5
$report.idle = Wait-Until { -not (Busy $app) } 180
$report.verified = Status $app

Click (Element $app "VerifyButton")
Wait-Until { Busy $app } 5 | Out-Null
$app.CloseMainWindow() | Out-Null
$report.closedDuringWork = $app.WaitForExit(30000)

$app = Open-Launcher $empty
Click (Element $app "VerifyButton")
Wait-Until { (Status $app) -ne $report.ready -and -not (Busy $app) } 30 | Out-Null
$report.error = Status $app

$nick = (Element $app "NickBox").GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$nick.SetValue("ab")
if ($nick.Current.Value -eq "ab") {
    Click (Element $app "PlayButton")
    Wait-Until { (Status $app) -ne $report.error } 10 | Out-Null
    $report.nick = Status $app
}

$nick.SetValue("Tester_1")
Click (Element $app "PlayButton")
Wait-Until { (Status $app) -ne $report.nick } 10 | Out-Null
$report.password = Status $app
(Element $app "PasswordField").GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("secret-pass")
$show = (Element $app "ShowPasswordBox").GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
$show.Toggle()
Wait-Until { (Element $app "PasswordText") -ne $null } 5 | Out-Null
$report.shown = (Element $app "PasswordText").GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
$show.Toggle()
Wait-Until { (Element $app "PasswordField") -ne $null } 5 | Out-Null
Click (Element $app "PlayButton")
Wait-Until { (Element $app "PasswordReset") -ne $null } 30 | Out-Null
Wait-Until { -not (Busy $app) } 30 | Out-Null
$report.saved = ((Element $app "PasswordReset") -ne $null) -and ((Element $app "PasswordField") -eq $null)
$report.afterPlay = Status $app
if ($shot) { Save-Shot $app ($shot -replace '\.png$', '_password.png') }
Click (Element $app "PasswordReset")
$report.reset = Wait-Until { (Element $app "PasswordField") -ne $null } 5

$second = Open-Launcher $empty
$labels = New-Object System.Windows.Automation.PropertyCondition($auto::ClassNameProperty, "Static")
$report.second = -join ($auto::FromHandle($second.MainWindowHandle).FindAll($scope, $labels) | ForEach-Object { $_.Current.Name })
$second.CloseMainWindow() | Out-Null
$report.secondExited = $second.WaitForExit(10000)
$report.firstAlive = -not $app.HasExited
$app.CloseMainWindow() | Out-Null
$report.firstExited = $app.WaitForExit(10000)

[System.IO.File]::WriteAllText($out, ($report | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
