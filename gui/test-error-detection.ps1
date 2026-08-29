<#
Guards the one rule that decides whether a player is told "Готово!" or "не полностью":
MainWindow.xaml.cs / IsProblemLine. Keep the pattern list below identical to the C# one.

Both sample sets are copied verbatim from real runs:
  - "broken" comes from a player's failed update (the manifest hashes were wrong, so nearly
    every file failed and packwiz's prompt got auto-answered into a cancel).
  - "healthy" comes from a full successful install; these lines must NOT be flagged, or every
    player gets told their working install is broken.

Run:  powershell -NoProfile -ExecutionPolicy Bypass -File gui\test-error-detection.ps1
#>

function Test-ProblemLine {
    param([string]$Line)
    return ($Line -match '(?i)hash invalid') -or
           ($Line -match '(?i)failed to download') -or
           ($Line -match '(?i)cancelled') -or
           ($Line -cmatch 'Exception') -or
           ($Line -match '(?i)error:')
}

# Each of these must be enough on its own to condemn a run. Bare stack-frame lines
# ("at link.infra.packwiz...") are deliberately absent: they carry no verdict by themselves,
# the "java.lang.Exception: ..." line above them is what marks the failure.
$broken = @(
    'java.lang.Exception: Hash invalid!'
    'Update cancelled by user!'
    'Failed to download mods/example.jar'
    'Error: connection reset'
)

$healthy = @(
    'Current version is: null'
    'New version is: v0.5.14'
    'Attempting to update...'
    'Update successful!'
    'Loading manifest file...'
    'Loading modpack file...'
    'Loading index file...'
    'Checking local files...'
    '(1/7572) Downloading config/fml.toml'
    '(7572/7572) Downloading tacz/gunpack_info.json'
    'Reloading modpack metadata...'
    'Finished successfully!'
    'Done!'
)

$fails = 0

foreach ($line in $broken) {
    if (-not (Test-ProblemLine $line)) {
        Write-Output "MISSED (would be reported as success): $line"
        $fails++
    }
}

foreach ($line in $healthy) {
    if (Test-ProblemLine $line) {
        Write-Output "FALSE POSITIVE (healthy run called broken): $line"
        $fails++
    }
}

if ($fails -eq 0) {
    Write-Output ("OK - {0} failure lines flagged, {1} healthy lines left alone." -f $broken.Count, $healthy.Count)
    exit 0
}
Write-Output "$fails problem(s)."
exit 1
