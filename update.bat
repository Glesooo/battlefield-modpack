@echo off
setlocal enabledelayedexpansion

rem === Battlefield modpack updater ===
rem One plain file, no compiled/obfuscated code inside - just batch commands and one
rem transparent PowerShell one-liner for the folder picker. Deliberately NOT a compiled .exe:
rem antivirus engines (Windows Defender included) heuristically flag PS2EXE-style compiled
rem wrappers as a class, regardless of what the wrapped script actually does, because that
rem exact packaging trick is heavily abused by real malware. A plain .bat has nothing for a
rem heuristic scanner to be suspicious of.
rem English-only text throughout on purpose: cmd.exe reads a .bat file's bytes using the
rem console's OEM codepage, and switching it mid-script with chcp does not reliably apply
rem retroactively to text already tokenized earlier in the same file - a well-known cmd.exe
rem quirk. Sidestepping non-ASCII entirely is simpler and more robust than fighting it.

set "SETTINGS_DIR=%APPDATA%\BattlefieldUpdater"
set "SETTINGS_FILE=%SETTINGS_DIR%\path.txt"
set "PACK_URL=https://raw.githubusercontent.com/Glesooo/battlefield-modpack/main/pack.toml"
set "BOOTSTRAP_URL=https://github.com/packwiz/packwiz-installer-bootstrap/releases/download/v0.0.3/packwiz-installer-bootstrap.jar"

set "INSTANCE="
if exist "%SETTINGS_FILE%" (
    set /p INSTANCE=<"%SETTINGS_FILE%"
)
if not "!INSTANCE!"=="" if not exist "!INSTANCE!" (
    echo Saved modpack folder no longer exists, pick it again.
    set "INSTANCE="
)

if "!INSTANCE!"=="" (
    powershell -NoProfile -Command "Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.MessageBox]::Show('Select your Battlefield modpack''s minecraft folder (the one containing mods, config, saves).', 'Battlefield Updater') | Out-Null" >nul

    for /f "usebackq delims=" %%P in (`powershell -NoProfile -Command "Add-Type -AssemblyName System.Windows.Forms; $d = New-Object System.Windows.Forms.FolderBrowserDialog; $d.Description = 'Battlefield modpack minecraft folder (with mods/config/saves)'; $d.ShowNewFolderButton = $false; if ($d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { $d.SelectedPath }"`) do set "INSTANCE=%%P"

    if "!INSTANCE!"=="" (
        echo No folder selected, exiting.
        pause
        exit /b 1
    )

    if not exist "%SETTINGS_DIR%" mkdir "%SETTINGS_DIR%"
    >"%SETTINGS_FILE%" echo !INSTANCE!
)

echo Modpack folder: !INSTANCE!

where java >nul 2>nul
if errorlevel 1 (
    echo.
    echo Java not found on this computer. Install Java ^(Minecraft needs it anyway^) and run this updater again.
    pause
    exit /b 1
)

cd /d "!INSTANCE!"

if not exist "battlefield-installer-bootstrap.jar" (
    echo Downloading updater tool...
    curl -L -o "battlefield-installer-bootstrap.jar" "%BOOTSTRAP_URL%"
    if errorlevel 1 (
        echo Failed to download the updater tool. Check your internet connection.
        pause
        exit /b 1
    )
)

echo Checking for updates...
java -jar "battlefield-installer-bootstrap.jar" "%PACK_URL%"

echo.
echo Done.
pause
