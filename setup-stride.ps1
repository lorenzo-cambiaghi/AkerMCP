# Builds the Stride adapter and copies it (plus its non-Stride dependencies)
# into Game Studio's AkerMcpPlugins drop-in folder, so the editor loads it.
#
# Usage:
#   .\setup-stride.ps1 -StrideBin "D:\stride\sources\editor\Stride.GameStudio\bin\Debug\net10.0-windows"
#   .\setup-stride.ps1 -StrideBin ... -NoBuild           # deploy existing output only
#   .\setup-stride.ps1 -StrideBin ... -Configuration Release
#
# StrideBin must point at the folder that contains Stride.GameStudio.exe
# (the same build the adapter was compiled against). There is no default: a guessed
# folder would build against, and deploy into, the wrong Game Studio.

param(
    [string]$StrideBin,
    [string]$Configuration = "Debug",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
$project = Join-Path $repoRoot "plugins\stride\AkerMcp.Stride.csproj"

if (-not $StrideBin) {
    throw "Pass -StrideBin pointing at your Game Studio build output (the folder with Stride.GameStudio.exe). For Stride installed from the Launcher, use install-stride-wrapper.ps1 instead."
}
$gameStudioExe = Join-Path $StrideBin "Stride.GameStudio.exe"
if (-not (Test-Path $gameStudioExe)) {
    throw "Stride.GameStudio.exe not found under '$StrideBin'. Pass -StrideBin pointing at your Game Studio build output."
}

# Build for the runtime this Game Studio runs on (a source build of 4.2 is .NET 8).
$tfm = "net10.0-windows"
$runtimeConfig = Join-Path $StrideBin "Stride.GameStudio.runtimeconfig.json"
if (Test-Path $runtimeConfig) {
    $runtimeTfm = (Get-Content $runtimeConfig -Raw | ConvertFrom-Json).runtimeOptions.tfm
    if ($runtimeTfm -match '^net\d+\.\d+$') { $tfm = "$runtimeTfm-windows" }
}

if (-not $NoBuild) {
    Write-Host "Building AkerMcp.Stride ($Configuration, $tfm) against $StrideBin ..."
    dotnet build $project -c $Configuration -p:StrideBin="$StrideBin" -p:StrideTfm=$tfm --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }
}

$src = Join-Path $repoRoot "plugins\stride\bin\$Configuration"
if (-not (Test-Path $src)) { throw "Build output not found: $src (run without -NoBuild?)." }

$dest = Join-Path $StrideBin "AkerMcpPlugins"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# Copy our assembly + its NuGet deps, but never Stride.*.dll - those already
# live next to Game Studio and must not be duplicated/shadowed.
$copied = @()
foreach ($dll in Get-ChildItem $src -Filter *.dll | Where-Object { $_.Name -notlike "Stride.*" }) {
    try {
        Copy-Item $dll.FullName -Destination $dest -Force
        $copied += $dll.Name
    }
    catch [System.IO.IOException] {
        throw "Could not overwrite '$($dll.Name)' - it is locked. Close Game Studio first (it loads these DLLs), then re-run."
    }
}

Write-Host "Deployed to $dest :"
$copied | ForEach-Object { Write-Host "  $_" }
Write-Host "Restart Game Studio (and reopen a project) to load the updated plugin."
