param($exe, $newer, $root, $source, $work, $out)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$auto = [System.Windows.Automation.AutomationElement]
$scope = [System.Windows.Automation.TreeScope]::Descendants
$report = @{}

function Wait-Until($condition, $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds 250
    }
    $false
}

function Text($process, $id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty, $id)
    $element = $auto::FromHandle($process.MainWindowHandle).FindFirst($scope, $condition)
    if ($element) { $element.Current.Name }
}

$target = Join-Path $work "MACE.exe"
$first = Start-Process $target -ArgumentList "--dir `"$root`" --source `"$source`"" -PassThru
$report.firstExited = $first.WaitForExit(120000)
$report.replaced = (Get-FileHash $target).Hash -eq (Get-FileHash $newer).Hash
$second = $null
$report.restarted = Wait-Until {
    $script:second = Get-Process -Name MACE -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $first.Id -and $_.Path -eq $target } | Select-Object -First 1
    if (-not $script:second) { return $false }
    $script:second.Refresh()
    $script:second.MainWindowHandle -ne 0
} 30
if ($second) {
    Wait-Until { (Text $second "VersionText") -ne $null } 10 | Out-Null
    Wait-Until { -not (Test-Path ($target + ".old")) } 15 | Out-Null
    $report.version = Text $second "VersionText"
    $report.status = Text $second "StageText"
    $report.oldRemoved = -not (Test-Path ($target + ".old"))
    $second.CloseMainWindow() | Out-Null
    $report.secondExited = $second.WaitForExit(10000)
}

[System.IO.File]::WriteAllText($out, ($report | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
