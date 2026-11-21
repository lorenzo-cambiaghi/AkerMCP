# Publishes an AkerMCP release: builds the packages, tags the commit,
# creates the GitHub Release and uploads the six artifacts.
#
# Release notes: -Notes wins; otherwise release-notes.md (gitignored, local) is used when
# it names the version being published; otherwise the commit list since the last tag.
#
# Usage:
#   .\publish-release.ps1 -Version v1.1.0
#   .\publish-release.ps1 -Version v1.1.0 -Notes "Custom release notes"
#   .\publish-release.ps1 -Version v1.1.0 -SkipBuild    # reuse existing Build/ output
#   .\publish-release.ps1 -Version v1.1.0 -DryRun      # everything except tag/release/upload
#   .\publish-release.ps1 -Version v1.1.0 -Resume -SkipBuild
#       # a run that stopped after pushing the tag: creates (or finds) the release and uploads
#       # what is missing; only publish-release.ps1 may have changed since the tag
#
# Auth: uses $env:GITHUB_TOKEN if set, otherwise extracts the stored
# credential from Git Credential Manager (the same one 'git push' uses).
#
# Requirements: Unity must be CLOSED during the build (batchmode export).

param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = "",
    [switch]$SkipBuild,
    [switch]$DryRun,
    [switch]$Resume
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
$repoSlug = "lorenzo-cambiaghi/AkerMCP"
Set-Location $repoRoot

if ($Version -notmatch '^v\d+\.\d+\.\d+$') {
    throw "Version must look like v1.2.3 (got '$Version')"
}

# --- Preflight ---------------------------------------------------------------

$existingTag = git tag -l $Version
if ($existingTag -and -not $Resume) {
    throw "Tag $Version already exists locally. Pick a new version, or -Resume a run that stopped after pushing it."
}
if ($Resume) {
    if (-not $existingTag) { throw "-Resume: there is no tag $Version to resume." }
    $localTag = git rev-parse $Version
    $remoteTag = ((git ls-remote --tags origin "refs/tags/$Version") -split "\s+")[0]
    if ($remoteTag -ne $localTag) {
        throw "-Resume: tag $Version on origin is '$remoteTag', locally $localTag. Resume only a tag this script pushed."
    }
    # What the release ships is the tagged commit. Fixing this script to get past the step that
    # failed is the one change allowed after it: anything else would be missing from the release.
    $since = @(git diff --name-only $Version HEAD | Where-Object { $_ -and $_ -ne "publish-release.ps1" })
    if ($since.Count -gt 0) {
        throw "-Resume: since $Version, HEAD also changed $($since -join ', '). Those changes would not be in the release."
    }
}

$dirty = git status --porcelain
if ($dirty) { throw "Working tree is not clean - commit or stash first:`n$dirty" }

# The csproj version is what the MCP handshake reports: a tag that disagrees with it ships
# binaries that call themselves the previous release.
$bare = $Version.Substring(1)
$csproj = [xml](Get-Content "$repoRoot\Server\AkerMcp.Server.csproj" -Raw)
$stamped = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
if ($stamped -ne $bare) {
    throw "Server/AkerMcp.Server.csproj is at '$stamped', not $bare. Stamp the version and commit first."
}
$godotCfg = Get-Content "$repoRoot\plugins\godot\plugin.cfg" -Raw
if ($godotCfg -notmatch "(?m)^version=`"$([regex]::Escape($bare))`"") {
    throw "plugins/godot/plugin.cfg does not declare version=`"$bare`" (Godot shows it in the plugin list)."
}
$unityPkg = Get-Content "$repoRoot\plugins\unity\package.json" -Raw | ConvertFrom-Json
if ($unityPkg.version -ne $bare) {
    throw "plugins/unity/package.json is at '$($unityPkg.version)', not $bare. Stamp the version and commit first."
}
$npmPkg = Get-Content "$repoRoot\packages\npm\package.json" -Raw | ConvertFrom-Json
if ($npmPkg.version -ne $bare) {
    throw "packages/npm/package.json is at '$($npmPkg.version)', not $bare. Stamp the version and commit first."
}
$unityWin = Get-Content "$repoRoot\plugins\unity\Editor\McpEditorWindow.cs" -Raw
if ($unityWin -notmatch "PluginVersion\s*=\s*`"$([regex]::Escape($bare))`"") {
    throw "plugins/unity/Editor/McpEditorWindow.cs does not declare PluginVersion = `"$bare`"."
}

$branch = git rev-parse --abbrev-ref HEAD
if ($branch -ne "main") {
    Write-Warning "Tagging '$branch', not main: remember to merge and push main so the public README matches."
}

if (-not $SkipBuild) {
    if (Test-Path "$repoRoot\samples\unity\Temp\UnityLockfile") {
        throw "Unity has the test project open. Close Unity first (batchmode export will fail)."
    }
    # build-package runs 'dotnet clean -c Release', which fails with MSB3027 halfway through
    # while an MCP session keeps the Release server running. Say so before starting.
    $releaseExe = [IO.Path]::GetFullPath("$repoRoot\Server\bin\Release")
    $locking = Get-Process AkerMcp.Server -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($releaseExe, [StringComparison]::OrdinalIgnoreCase) }
    if ($locking) {
        throw "AkerMcp.Server (Release) is running as PID $($locking.Id -join ', ') - an MCP session holds the exe. " +
              "Close those sessions (or stop the processes) before building."
    }
}

# Resolve the notes now, so a stale release-notes.md stops the run before a long build.
$notesFile = Join-Path $repoRoot "release-notes.md"
if (-not $Notes -and (Test-Path $notesFile)) {
    $fileNotes = Get-Content $notesFile -Raw -Encoding UTF8
    if ($fileNotes -notmatch [regex]::Escape($Version)) {
        throw "release-notes.md does not mention $Version (left over from another release?). " +
              "Update it, pass -Notes, or delete it to publish the commit list."
    }
    $Notes = $fileNotes
    Write-Host "Release notes: release-notes.md"
}

# --- Token -------------------------------------------------------------------

$token = $env:GITHUB_TOKEN
if (-not $token) {
    # PS 5.1 piping to native stdin is unreliable; go through a temp file + cmd.
    $credIn = Join-Path $env:TEMP "akermcp_cred_in.txt"
    [IO.File]::WriteAllText($credIn, "protocol=https`nhost=github.com`n`n")
    try {
        $credOut = cmd /c "git credential fill < `"$credIn`""
        $token = ($credOut | Where-Object { $_ -like "password=*" }) -replace "^password="
    }
    finally {
        Remove-Item $credIn -Force -ErrorAction SilentlyContinue
    }
}
if (-not $token) { throw "No GitHub token: set GITHUB_TOKEN or store credentials via git push once." }

$apiHeaders = @{ Authorization = "Bearer $token"; Accept = "application/vnd.github+json" }
$who = Invoke-RestMethod "https://api.github.com/user" -Headers $apiHeaders
Write-Host "Authenticated as $($who.login)"

# --- Build -------------------------------------------------------------------

$assets = @(
    @{ Name = "AkerMCP.unitypackage";         ContentType = "application/octet-stream" },
    @{ Name = "AkerMcp.Godot-addon.zip";      ContentType = "application/zip" },
    @{ Name = "AkerMcp.Stride-source.zip";    ContentType = "application/zip" },
    @{ Name = "AkerMcp.Server-win-x64.zip";   ContentType = "application/zip" },
    @{ Name = "AkerMcp.Server-osx-x64.zip";   ContentType = "application/zip" },
    @{ Name = "AkerMcp.Server-linux-x64.zip"; ContentType = "application/zip" }
)

if (-not $SkipBuild) {
    Write-Host "Building packages (this takes a few minutes)..."
    cmd /c "`"$repoRoot\build-package.bat`""
    if ($LASTEXITCODE -ne 0) { throw "build-package.bat failed (exit $LASTEXITCODE)" }
}

foreach ($a in $assets) {
    $path = Join-Path "$repoRoot\Build" $a.Name
    if (-not (Test-Path $path)) { throw "Missing artifact: $path (run without -SkipBuild?)" }
}
Write-Host "All $($assets.Count) artifacts present in Build/"

# --- Release notes -----------------------------------------------------------

if (-not $Notes) {
    $lastTag = git tag --sort=-v:refname | Select-Object -First 1
    if ($lastTag) {
        $log = (git log "$lastTag..HEAD" --pretty=format:"- %s") -join "`n"
        $Notes = "## Changes since $lastTag`n$log"
    }
    else {
        $Notes = "AkerMCP $Version"
    }
    $Notes += "`n`nSee the [Quick Start](https://github.com/$repoSlug#quick-start-recommended) for setup instructions."
}

if ($DryRun) {
    Write-Host "`n--- DRY RUN: would tag $Version and create release with notes: ---"
    Write-Host $Notes
    Write-Host "--- and upload: $($assets.Name -join ', ') ---"
    exit 0
}

# --- Tag + Release + Upload --------------------------------------------------

if (-not $Resume) {
    git tag $Version
    git push origin $Version
    if ($LASTEXITCODE -ne 0) { throw "Failed to push tag $Version" }
}

$release = $null
if ($Resume) {
    try {
        $release = Invoke-RestMethod "https://api.github.com/repos/$repoSlug/releases/tags/$Version" -Headers $apiHeaders
        Write-Host "Release found: $($release.html_url)"
    }
    catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        if ($status -ne 404) { throw }
    }
}

if (-not $release) {
    $body = @{ tag_name = $Version; name = "AkerMCP $Version"; body = $Notes } | ConvertTo-Json
    # Windows PowerShell 5.1 sends a string body as ISO-8859-1. An e with an acute accent in the
    # notes went out as a byte that is not UTF-8 and GitHub refused the JSON (v1.7.0, "Problems
    # parsing JSON"); an arrow, which ISO-8859-1 cannot hold, became "?" without an error (v1.6.0's
    # notes on GitHub). Bytes go out as they are.
    $bodyBytes = [System.Text.Encoding]::UTF8.GetBytes($body)
    $release = Invoke-RestMethod "https://api.github.com/repos/$repoSlug/releases" `
        -Method Post -Headers $apiHeaders -Body $bodyBytes -ContentType "application/json; charset=utf-8"
    Write-Host "Release created: $($release.html_url)"
}

foreach ($a in $assets) {
    $path = Join-Path "$repoRoot\Build" $a.Name
    $existing = @($release.assets | Where-Object { $_.name -eq $a.Name })
    if ($existing.Count -gt 0 -and $existing[0].state -eq "uploaded") {
        Write-Host "$($a.Name) is already uploaded."
        continue
    }
    if ($existing.Count -gt 0) {
        # An upload cut off halfway leaves an asset that blocks the name: remove it and start over.
        Invoke-RestMethod "https://api.github.com/repos/$repoSlug/releases/assets/$($existing[0].id)" `
            -Method Delete -Headers $apiHeaders | Out-Null
    }
    $uri = "https://uploads.github.com/repos/$repoSlug/releases/$($release.id)/assets?name=$($a.Name)"
    Write-Host "Uploading $($a.Name)..."
    $uploaded = Invoke-RestMethod $uri -Method Post `
        -Headers @{ Authorization = "Bearer $token" } `
        -ContentType $a.ContentType -InFile $path
    Write-Host "  -> $($uploaded.state) ($([math]::Round($uploaded.size / 1MB, 1)) MB)"
}

Write-Host "`nDone: $($release.html_url)"
