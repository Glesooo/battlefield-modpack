@echo off
setlocal

rem === Battlefield modpack updater ===
rem Place this file directly in your instance's "minecraft" folder
rem (PrismLauncher\instances\<YourInstance>\minecraft\update.bat) and double-click it
rem whenever there's an update. It only downloads what actually changed.

set "PACK_URL=https://raw.githubusercontent.com/OWNER/REPO/main/pack.toml"
set "BOOTSTRAP=packwiz-installer-bootstrap.jar"

if not exist "%BOOTSTRAP%" (
    echo Downloading updater tool...
    curl -L -o "%BOOTSTRAP%" "https://github.com/packwiz/packwiz-installer-bootstrap/releases/download/v0.0.3/packwiz-installer-bootstrap.jar"
    if errorlevel 1 (
        echo Failed to download the updater. Check your internet connection.
        pause
        exit /b 1
    )
)

echo Checking for updates...
java -jar "%BOOTSTRAP%" "%PACK_URL%"

echo.
echo Done.
pause
