# Publishes an AkerMCP release: builds the packages, tags the commit,
# creates the GitHub Release and uploads the four artifacts.
#
# Usage:
#   .\publish-release.ps1 -Version v1.1.0
#   .\publish-release.ps1 -Version v1.1.0 -Notes "Custom release notes"
#   .\publish-release.ps1 -Version v1.1.0 -SkipBuild    # reuse existing Build/ output
#   .\publish-release.ps1 -Version v1.1.0 -DryRun      # everything except tag/release/upload
#
# Auth: uses $env:GITHUB_TOKEN if set, otherwise extracts the stored
# credential from Git Credential Manager (the same one 'git push' uses).
#
# Requirements: Unity must be CLOSED during the build (batchmode export).

param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = "",
    [switch]$SkipBuild,
    [switch]$DryRun
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
if ($existingTag) { throw "Tag $Version already exists locally. Pick a new version." }

$dirty = git status --porcelain
if ($dirty) { throw "Working tree is not clean - commit or stash first:`n$dirty" }

if (-not $SkipBuild) {
    if (Test-Path "$repoRoot\UnityTestProject\Temp\UnityLockfile") {
        throw "Unity has the test project open. Close Unity first (batchmode export will fail)."
    }
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
Write-Host "All 4 artifacts present in Build/"

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

git tag $Version
git push origin $Version
if ($LASTEXITCODE -ne 0) { throw "Failed to push tag $Version" }

$body = @{ tag_name = $Version; name = "AkerMCP $Version"; body = $Notes } | ConvertTo-Json
$release = Invoke-RestMethod "https://api.github.com/repos/$repoSlug/releases" `
    -Method Post -Headers $apiHeaders -Body $body -ContentType "application/json"
Write-Host "Release created: $($release.html_url)"

foreach ($a in $assets) {
    $path = Join-Path "$repoRoot\Build" $a.Name
    $uri = "https://uploads.github.com/repos/$repoSlug/releases/$($release.id)/assets?name=$($a.Name)"
    Write-Host "Uploading $($a.Name)..."
    $uploaded = Invoke-RestMethod $uri -Method Post `
        -Headers @{ Authorization = "Bearer $token" } `
        -ContentType $a.ContentType -InFile $path
    Write-Host "  -> $($uploaded.state) ($([math]::Round($uploaded.size / 1MB, 1)) MB)"
}

Write-Host "`nDone: $($release.html_url)"
