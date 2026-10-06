param($exe, $root, $source, $nick, $out, [int]$TimeoutSec = 540)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$auto = [System.Windows.Automation.AutomationElement]
$scope = [System.Windows.Automation.TreeScope]::Descendants

function Element($process, $id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty, $id)
    $auto::FromHandle($process.MainWindowHandle).FindFirst($scope, $condition)
}

$started = Get-Date
$launcher = Start-Process $exe -ArgumentList "--dir `"$root`" --source `"$source`"" -PassThru
while ($launcher.MainWindowHandle -eq 0 -and ((Get-Date) - $started).TotalSeconds -lt 20) {
    Start-Sleep -Milliseconds 200
    $launcher.Refresh()
}

$box = (Element $launcher "NickBox").GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$box.SetValue($nick)
(Element $launcher "PlayButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

$timeline = New-Object System.Collections.Generic.List[string]
$last = ""
while (-not $launcher.HasExited -and ((Get-Date) - $started).TotalSeconds -lt $TimeoutSec) {
    try {
        $stage = (Element $launcher "StageText").Current.Name
        if ($stage -ne $last) {
            $timeline.Add(("{0,4:N0}s {1}" -f ((Get-Date) - $started).TotalSeconds, $stage))
            $last = $stage
        }
    } catch {
    }
    Start-Sleep -Milliseconds 300
    $launcher.Refresh()
}
$timeline.Add(("{0,4:N0}s launcher exited={1}" -f ((Get-Date) - $started).TotalSeconds, $launcher.HasExited))
[System.IO.File]::WriteAllLines($out, $timeline, (New-Object System.Text.UTF8Encoding($false)))
if ($launcher.HasExited) { exit 0 } else { exit 1 }
