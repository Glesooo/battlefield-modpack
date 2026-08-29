<#
.SYNOPSIS
  Publishes the current state of the M.A.C.E instance (mods, gunpacks, shared client config)
  as an update players can pull with update.bat.

.DESCRIPTION
  Two kinds of tracked content:
  - Big binaries (mod jars in mods/, gunpack zips in tacz/) are uploaded to a GitHub Release
    that acts as a file bucket; only a small packwiz .toml pointer (name+hash+url) is
    committed to git. Keeps the repo itself tiny no matter how large the pack gets.
  - Small files (the shared client config/, and whatever isn't a *.zip inside tacz/ - a few
    gunpack mods auto-export a companion folder there on first run) are copied straight into
    git as raw files. packwiz's default behaviour is to overwrite anything that doesn't match
    the tracked hash, which is exactly "force everyone onto my config" with no extra flag.

  Diffing is by SHA-256 against what's already tracked, so re-running this after a small
  change (e.g. only HudElements' jar changed) only re-uploads that one file.

  Deliberately NOT touched: saves/ (player's own world) and saves/<world>/serverconfig/
  (Forge 1.20.1 keeps server-side configs like FactionConfig/DeathmatchConfig inside the save
  itself, not in the shared instance folder - that's per-player data, not part of the pack).

.PARAMETER Repo
  Your GitHub repo as "owner/name", e.g. "Gleso/battlefield-modpack". Required.

.EXAMPLE
  .\publish.ps1 -Repo "Gleso/battlefield-modpack"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Repo,
    [string]$InstancePath = "C:\Users\Gleso\AppData\Roaming\PrismLauncher\instances\M.A.C.E\minecraft",
    [string]$AssetTag = "mod-files"
)

$ErrorActionPreference = "Stop"
$packwiz = "$env:USERPROFILE\go\bin\packwiz.exe"
$distRoot = $PSScriptRoot

if (-not (Test-Path $packwiz)) {
    throw "packwiz.exe not found at $packwiz - see ModpackDist/README.md."
}

# ---------------------------------------------------------------- asset bucket
# Redirecting a native command's stderr while $ErrorActionPreference = "Stop" is set turns its
# normal "release not found" message into a terminating error instead of just a non-zero exit
# code - drop to "Continue" for this one check so the expected-to-fail-on-first-run case
# actually reaches the $LASTEXITCODE check below.
$prevEAP = $ErrorActionPreference
$ErrorActionPreference = "Continue"
gh release view $AssetTag --repo $Repo *> $null
$ErrorActionPreference = $prevEAP
if ($LASTEXITCODE -ne 0) {
    Write-Host "Creating release '$AssetTag' to hold binary files..."
    gh release create $AssetTag --repo $Repo --title "Pack files" `
        --notes "Binary files referenced by pack.toml. Don't rename or delete assets by hand - publish.ps1 manages this release."
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed - is 'gh auth login' done and does the repo exist?" }
}

<#
Syncs one flat folder of binary files (mods/*.jar, tacz/*.zip) as packwiz url-metafiles
pointing at the shared release. $MetaFolder controls both where the .toml pointer lands in
this repo AND where packwiz-installer places the real file on the player's machine (packwiz
mirrors metadata-folder structure onto the client's game folder - that's the mechanism, not
just repo tidiness).
#>
function Sync-BinaryFolder {
    param(
        [string]$LiveDir,
        [string]$FileFilter,
        [string]$MetaFolder
    )

    $trackedDir = Join-Path $distRoot $MetaFolder
    New-Item -ItemType Directory -Path $trackedDir -Force | Out-Null

    $liveFiles = @(Get-ChildItem $LiveDir -Filter $FileFilter -File -ErrorAction SilentlyContinue)
    $liveNames = @($liveFiles.Name)

    $tracked = @(Get-ChildItem $trackedDir -Filter *.toml -ErrorAction SilentlyContinue | ForEach-Object {
        $toml = Get-Content $_.FullName -Raw
        if ($toml -match 'filename\s*=\s*"([^"]+)"') {
            $hash = $null
            if ($toml -match 'hash\s*=\s*"([0-9a-fA-F]+)"') { $hash = $Matches[1] }
            [PSCustomObject]@{ Meta = $_.FullName; Filename = $Matches[1]; Hash = $hash }
        }
    })

    foreach ($f in $liveFiles) {
        $localHash = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
        $existing = $tracked | Where-Object { $_.Filename -eq $f.Name } | Select-Object -First 1

        if ($existing -and $existing.Hash -and ($existing.Hash -ieq $localHash)) {
            continue # unchanged
        }

        # Filesystem-safe name for BOTH the uploaded asset and the metadata file - several
        # jar/zip names in this pack have characters (backtick, brackets, +, spaces) that trip
        # up either `gh release upload`'s own glob expansion (square brackets = a wildcard
        # character class to it, so it just doesn't find the file) or packwiz's slug generator.
        # Uploading a same-content temp copy under the sanitized name sidesteps both, rather
        # than fighting each tool's own quoting/escaping rules.
        $safeName = ($f.BaseName -replace '[^a-zA-Z0-9._-]', '_')
        $safeFileName = "$safeName$($f.Extension)"
        $tempCopy = Join-Path ([System.IO.Path]::GetTempPath()) $safeFileName
        Copy-Item $f.FullName $tempCopy -Force

        Write-Host "Uploading $($f.Name) (as $safeFileName) ..."
        try {
            gh release upload $AssetTag $tempCopy --repo $Repo --clobber
            if ($LASTEXITCODE -ne 0) { throw "gh release upload failed for $($f.Name)" }
        } finally {
            Remove-Item $tempCopy -Force -ErrorAction SilentlyContinue
        }

        if ($existing) { Remove-Item $existing.Meta -Force }

        $url = "https://github.com/$Repo/releases/download/$AssetTag/" + [uri]::EscapeDataString($safeFileName)

        Push-Location $distRoot
        & $packwiz url add $f.BaseName $url --meta-folder $MetaFolder --meta-name $safeName -y
        Pop-Location
        if ($LASTEXITCODE -ne 0) { throw "packwiz url add failed for $($f.Name)" }
    }

    foreach ($t in $tracked) {
        if ($liveNames -notcontains $t.Filename) {
            Write-Host "Removing $($t.Filename) (no longer present) ..."
            Remove-Item $t.Meta -Force
        }
    }
}

# ---------------------------------------------------------------- mods (whole folder is jars)
Sync-BinaryFolder -LiveDir (Join-Path $InstancePath "mods") -FileFilter "*.jar" -MetaFolder "mods"

# ---------------------------------------------------------------- gunpacks (only the *.zip ones -
# the unpacked subfolders next to them are handled below, as raw files)
Sync-BinaryFolder -LiveDir (Join-Path $InstancePath "tacz") -FileFilter "*.zip" -MetaFolder "tacz"

<#
Raw-copies a whole tree, replacing the destination outright each time (packwiz then hashes
whatever it finds and syncs it byte-for-byte to players - "force everyone onto my copy").
$Exclude is a list of top-level filenames/extensions to skip (used to keep the tacz/*.zip
gunpacks - handled above as url-metafiles - out of the raw copy).
#>
function Sync-RawFolder {
    param(
        [string]$SrcDir,
        [string]$DstDir,
        [string[]]$ExcludeExtensions = @(),   # skip these when copying FROM source
        [string[]]$PreserveExtensions = @()   # never touch these when clearing DstDir - lets
                                               # this share a folder with Sync-BinaryFolder's
                                               # .toml pointers (tacz/ holds both)
    )
    if (-not (Test-Path $SrcDir)) { return }
    New-Item -ItemType Directory -Path $DstDir -Force | Out-Null

    # Clear stale content first so removals on the source side propagate, but never touch
    # files owned by Sync-BinaryFolder.
    Get-ChildItem $DstDir -Force | ForEach-Object {
        if ($PreserveExtensions -contains $_.Extension) { return }
        Remove-Item $_.FullName -Recurse -Force
    }

    Get-ChildItem $SrcDir -Force | ForEach-Object {
        if ($_.PSIsContainer) {
            Copy-Item $_.FullName -Destination $DstDir -Recurse -Force
        } elseif ($ExcludeExtensions -notcontains $_.Extension) {
            Copy-Item $_.FullName -Destination $DstDir -Force
        }
    }
}

# Shared client config only - see the "deliberately NOT touched" note above for why
# saves/<world>/serverconfig is excluded (it isn't even reachable from $InstancePath/config).
Sync-RawFolder -SrcDir (Join-Path $InstancePath "config") -DstDir (Join-Path $distRoot "config")

# Everything in tacz/ that ISN'T one of the big zip gunpacks (a few gunpack mods auto-export
# a small companion folder here on first run - cheap to just mirror as-is). PreserveExtensions
# keeps this from wiping the .toml pointers Sync-BinaryFolder just wrote into the same folder.
Sync-RawFolder -SrcDir (Join-Path $InstancePath "tacz") -DstDir (Join-Path $distRoot "tacz") `
    -ExcludeExtensions @(".zip") -PreserveExtensions @(".toml")

# ---------------------------------------------------------------- refresh + publish
Push-Location $distRoot
& $packwiz refresh
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "packwiz refresh failed" }

git add -A
$pending = git status --porcelain
if (-not $pending) {
    Write-Host "Nothing changed - nothing to publish."
    Pop-Location
    return
}

git commit -m "Update $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
git push
Pop-Location

Write-Host ""
Write-Host "Published. Players just need to run update.bat."
