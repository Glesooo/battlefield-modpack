# Battlefield modpack updater.
# Place BattlefieldUpdater.exe directly in your instance's "minecraft" folder
# (PrismLauncher\instances\<YourInstance>\minecraft\BattlefieldUpdater.exe) and double-click
# it whenever there's an update. It only downloads what actually changed.

$PackUrl = "https://raw.githubusercontent.com/Glesooo/battlefield-modpack/main/pack.toml"
$BootstrapUrl = "https://github.com/packwiz/packwiz-installer-bootstrap/releases/download/v0.0.3/packwiz-installer-bootstrap.jar"

# Always operate next to the exe itself, not whatever directory it happened to be launched
# from - a compiled exe's own working directory isn't as predictable as a .bat's, so this is
# resolved explicitly rather than trusted.
$exeDir = Split-Path -Parent ([System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName)
Set-Location $exeDir

if (-not (Get-Command java -ErrorAction SilentlyContinue)) {
    Write-Host "Java not found on PATH. Install/enable Java (Minecraft needs it anyway) and try again." -ForegroundColor Red
    Read-Host "Press Enter to close"
    exit 1
}

$bootstrap = Join-Path $exeDir "packwiz-installer-bootstrap.jar"
if (-not (Test-Path $bootstrap)) {
    Write-Host "Downloading updater tool..."
    try {
        Invoke-WebRequest -Uri $BootstrapUrl -OutFile $bootstrap
    } catch {
        Write-Host "Failed to download the updater tool. Check your internet connection." -ForegroundColor Red
        Write-Host $_.Exception.Message
        Read-Host "Press Enter to close"
        exit 1
    }
}

Write-Host "Checking for updates..."
& java -jar $bootstrap $PackUrl
$exitCode = $LASTEXITCODE

Write-Host ""
if ($exitCode -eq 0) {
    Write-Host "Done - you're up to date." -ForegroundColor Green
} else {
    Write-Host "The updater reported a problem (exit code $exitCode) - scroll up for details." -ForegroundColor Yellow
}
Read-Host "Press Enter to close"
