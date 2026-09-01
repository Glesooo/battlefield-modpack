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

if (-not (Test-Path -LiteralPath $packwiz)) {
    throw "packwiz.exe not found at $packwiz - see ModpackDist/README.md."
}

if (-not (Test-Path -LiteralPath $InstancePath)) {
    throw "Instance folder not found: $InstancePath - publishing from here would wipe config/ out of the pack."
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
The one place the sanitized name is defined. It is what gets uploaded as the release asset,
what packwiz records as the metafile's `filename`, and therefore the name the file ends up
with on a player's disk - so every comparison against a metafile must go through here too.
#>
function Get-SafeName {
    param([System.IO.FileInfo]$File)
    ($File.BaseName -replace '[^a-zA-Z0-9._-]', '_') + $File.Extension
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

    $liveFiles = @(Get-ChildItem -LiteralPath $LiveDir -Filter $FileFilter -File -ErrorAction SilentlyContinue)
    # Compare against SANITIZED names, never the raw ones: a metafile's `filename` is always the
    # sanitized form (see $safeName below), so matching it against the live folder's original
    # names never hits for any mod whose name needed sanitizing - which made this function
    # re-upload those files on every run AND then delete their freshly-written metafiles in the
    # stale-entry sweep at the bottom, silently dropping them out of the pack.
    $liveNames = @($liveFiles | ForEach-Object { (Get-SafeName $_) })

    # Two different files can sanitize to one published name ("mod+1.jar" and "mod_1.jar" both
    # become "mod_1.jar"). That would upload one over the other and overwrite one metafile with
    # the other's, silently dropping a mod from the pack - so refuse to publish instead of
    # guessing which one was meant. Very reachable here: the pack briefly contained exactly such
    # pairs after an updater run wrote sanitized copies alongside the originals.
    $collisions = @($liveFiles | Group-Object { Get-SafeName $_ } | Where-Object { $_.Count -gt 1 })
    if ($collisions.Count -gt 0) {
        $detail = ($collisions | ForEach-Object {
            "  $($_.Name) <- " + (($_.Group | ForEach-Object { $_.Name }) -join ' , ')
        }) -join [Environment]::NewLine
        throw "Different files in $LiveDir publish under the same name - rename or remove one of each pair:$([Environment]::NewLine)$detail"
    }

    $tracked = @(Get-ChildItem -LiteralPath $trackedDir -Filter *.toml -ErrorAction SilentlyContinue | ForEach-Object {
        $toml = Get-Content -LiteralPath $_.FullName -Raw
        # Capture the filename BEFORE running the second -match: $Matches is a single automatic
        # variable that every -match overwrites, so reading $Matches[1] after the hash match
        # yields the hash, not the filename. That silently turned every Filename into a hash
        # string, so nothing ever matched a live file - which made the stale sweep below treat
        # every existing metafile as orphaned and delete it.
        $nameMatch = [regex]::Match($toml, 'filename\s*=\s*"([^"]+)"')
        if ($nameMatch.Success) {
            $hashMatch = [regex]::Match($toml, 'hash\s*=\s*"([0-9a-fA-F]+)"')
            $hash = if ($hashMatch.Success) { $hashMatch.Groups[1].Value } else { $null }
            [PSCustomObject]@{ Meta = $_.FullName; Filename = $nameMatch.Groups[1].Value; Hash = $hash }
        }
    })

    # Refuse to read "I found nothing" as "the author deleted everything". The Get-ChildItem above
    # silences every failure - folder renamed, drive offline, AV lock, OneDrive placeholder - and an
    # empty result would send the stale sweep at the bottom through every metafile, publish an empty
    # index and report success. packwiz-installer then deletes those files from every player's game.
    # Publishing a genuinely empty mods folder is not something anyone does by accident.
    if ($liveFiles.Count -eq 0 -and $tracked.Count -gt 0) {
        throw "No $FileFilter files found in $LiveDir, but $($tracked.Count) are currently published. Refusing to publish: this would delete them from every player's install. Check that the folder exists and is readable."
    }

    # Names actually present on the release, used by the skip test below.
    $releaseAssets = @((gh release view $AssetTag --repo $Repo --json assets | ConvertFrom-Json).assets.name)
    if ($LASTEXITCODE -ne 0) { throw "gh release view failed - cannot confirm which assets exist" }

    foreach ($f in $liveFiles) {
        # -LiteralPath everywhere a real (possibly bracket/backtick-containing) filename is
        # used: PowerShell's own -Path parameters wildcard-expand by default, so e.g.
        # "...[1.20-1.20.5].jar" gets read as a character-class glob and silently matches
        # nothing - the exact bug this project already hit once with Test-Path (see
        # Libs/README.md history). Every cmdlet below is pinned to -LiteralPath/-Destination
        # rather than relying on positional binding picking the literal-safe parameter.
        # Filesystem-safe name for BOTH the uploaded asset and the metadata file - several
        # jar/zip names in this pack have characters (backtick, brackets, +, spaces) that trip
        # up either `gh release upload`'s own glob expansion (square brackets = a wildcard
        # character class to it, so it just doesn't find the file) or packwiz's slug generator.
        # Uploading a same-content temp copy under the sanitized name sidesteps both, rather
        # than fighting each tool's own quoting/escaping rules.
        $safeFileName = Get-SafeName $f
        $safeName = [System.IO.Path]::GetFileNameWithoutExtension($safeFileName)

        $localHash = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
        $existing = $tracked | Where-Object { $_.Filename -eq $safeFileName } | Select-Object -First 1

        # The asset check is not redundant with the hash check: an interrupted run can leave a
        # metafile behind whose upload never finished, and trusting the metafile alone would
        # skip that file on every later run, leaving players with a permanent 404.
        if ($existing -and $existing.Hash -and ($existing.Hash -ieq $localHash) -and
            ($releaseAssets -contains $safeFileName)) {
            continue # unchanged and actually present on the release
        }

        $tempCopy = Join-Path ([System.IO.Path]::GetTempPath()) $safeFileName
        Copy-Item -LiteralPath $f.FullName -Destination $tempCopy -Force

        Write-Host "Uploading $($f.Name) (as $safeFileName) ..."
        try {
            gh release upload $AssetTag $tempCopy --repo $Repo --clobber
            if ($LASTEXITCODE -ne 0) { throw "gh release upload failed for $($f.Name)" }
        } finally {
            Remove-Item -LiteralPath $tempCopy -Force -ErrorAction SilentlyContinue
        }

        if ($existing) { Remove-Item -LiteralPath $existing.Meta -Force }

        $url = "https://github.com/$Repo/releases/download/$AssetTag/" + [uri]::EscapeDataString($safeFileName)

        Push-Location $distRoot
        & $packwiz url add $f.BaseName $url --meta-folder $MetaFolder --meta-name $safeName -y
        Pop-Location
        if ($LASTEXITCODE -ne 0) { throw "packwiz url add failed for $($f.Name)" }
    }

    foreach ($t in $tracked) {
        if ($liveNames -notcontains $t.Filename) {
            Write-Host "Removing $($t.Filename) (no longer present) ..."
            Remove-Item -LiteralPath $t.Meta -Force
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
    if (-not (Test-Path -LiteralPath $SrcDir)) { return }
    New-Item -ItemType Directory -Path $DstDir -Force | Out-Null

    # Same hazard as Sync-BinaryFolder's empty-folder guard, one step earlier: the source existing
    # is not the same as the source having anything in it. Clearing the destination and copying
    # nothing publishes an empty config/, which packwiz-installer then deletes from every player.
    $srcEntries = @(Get-ChildItem -LiteralPath $SrcDir -Force -ErrorAction SilentlyContinue)
    $dstKept = @(Get-ChildItem -LiteralPath $DstDir -Force -ErrorAction SilentlyContinue |
        Where-Object { $PreserveExtensions -notcontains $_.Extension })
    if ($srcEntries.Count -eq 0 -and $dstKept.Count -gt 0) {
        throw "$SrcDir is empty but $($dstKept.Count) published files come from it. Refusing to publish: this would delete them from every player's install."
    }

    # Clear stale content first so removals on the source side propagate, but never touch
    # files owned by Sync-BinaryFolder. -LiteralPath throughout - see the comment in
    # Sync-BinaryFolder on why (real filenames here aren't guaranteed glob-safe either).
    Get-ChildItem -LiteralPath $DstDir -Force | ForEach-Object {
        if ($PreserveExtensions -contains $_.Extension) { return }
        Remove-Item -LiteralPath $_.FullName -Recurse -Force
    }

    Get-ChildItem -LiteralPath $SrcDir -Force | ForEach-Object {
        if ($_.PSIsContainer) {
            Copy-Item -LiteralPath $_.FullName -Destination $DstDir -Recurse -Force
        } elseif ($ExcludeExtensions -notcontains $_.Extension) {
            Copy-Item -LiteralPath $_.FullName -Destination $DstDir -Force
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
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "git add failed (exit $LASTEXITCODE)" }
$pending = git status --porcelain
if (-not $pending) {
    # "Nothing to commit" is not the same as "GitHub has it". A previous run whose push failed
    # leaves the commit sitting locally, and every later run then finds nothing to stage and
    # reports success while players stay on the old pack. Verify the remote before believing it.
    $localHead = (git rev-parse HEAD).Trim()
    $remoteHead = ((git ls-remote origin HEAD) -split '\s+')[0]
    if ($localHead -ne $remoteHead) {
        Write-Host "Nothing new to commit, but GitHub is behind - pushing the existing commit..."
        git push
        if ($LASTEXITCODE -ne 0) { Pop-Location; throw "git push failed (exit $LASTEXITCODE) - players will NOT see the latest commit." }
        $remoteHead = ((git ls-remote origin HEAD) -split '\s+')[0]
        if ($localHead -ne $remoteHead) { Pop-Location; throw "Push reported success but GitHub is still on $remoteHead." }
        Pop-Location
        Write-Host "Pushed. Players just need to run the updater."
        return
    }
    Write-Host "Nothing changed - nothing to publish (GitHub already has $($localHead.Substring(0,7)))."
    Pop-Location
    return
}

# A native command's non-zero exit does NOT stop the script even under
# $ErrorActionPreference = "Stop" - only PowerShell's own cmdlets honour that. Without these
# explicit checks a failed push (expired auth, no network, someone else pushed first) still
# printed "Published", so the pack silently stayed on the old version while everyone was told
# to update.
git commit -m "Update $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "git commit failed (exit $LASTEXITCODE) - nothing was published." }

git push
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "git push failed (exit $LASTEXITCODE) - the commit exists locally but players will NOT see it. Fix the error and re-run." }

# Confirm the remote really moved to what we just committed, rather than trusting exit codes
# alone - the push output goes to stderr and is easy to misread as failure (or success).
$localHead = (git rev-parse HEAD).Trim()
$remoteHead = ((git ls-remote origin HEAD) -split '\s+')[0]
Pop-Location
if ($localHead -ne $remoteHead) {
    throw "Push reported success but GitHub is on $remoteHead while this commit is $localHead - players would get the old pack."
}

Write-Host ""
Write-Host "Published and verified on GitHub ($($localHead.Substring(0,7))). Players just need to run the updater."
