# Installs the AkerMcp Stride adapter into a STOCK (binary) Game Studio, with no
# rebuild of Stride. It builds the adapter + startup hook + launcher, drops them
# into <GameStudio>/AkerMcpPlugins, and creates a "Stride Game Studio (AkerMCP)"
# shortcut that launches Game Studio with the plugin injected FOR THAT RUN ONLY
# (DOTNET_STARTUP_HOOKS is set in the child process environment, never machine- or
# user-wide). Nothing is written to the system; uninstall just deletes the files.
#
# Usage:
#   .\install-stride-wrapper.ps1 -GameStudioPath "C:\...\Stride.GameStudio.exe"
#   .\install-stride-wrapper.ps1                 # try to auto-detect Game Studio
#   .\install-stride-wrapper.ps1 -Uninstall
#   .\install-stride-wrapper.ps1 -Configuration Debug
#
# Source builds of Stride use a different path (Program.cs loader + setup-stride.ps1);
# this script is only for users who installed Stride from the official Launcher.

param(
    [string]$GameStudioPath,
    [string]$Configuration = "Release",
    [switch]$NoBuild,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot

$ShortcutName = "Stride Game Studio (AkerMCP).lnk"
$desktopLnk   = Join-Path ([Environment]::GetFolderPath("Desktop")) $ShortcutName
$startMenuLnk = Join-Path ([Environment]::GetFolderPath("Programs")) $ShortcutName

function Resolve-GameStudioExe {
    param([string]$Path)

    if ($Path) {
        if (Test-Path $Path -PathType Container) {
            $Path = Join-Path $Path "Stride.GameStudio.exe"
        }
        if (-not (Test-Path $Path)) {
            throw "Stride.GameStudio.exe not found at '$Path'."
        }
        return (Resolve-Path $Path).Path
    }

    # Best-effort auto-detect of a Launcher-installed Game Studio (newest first).
    $roots = @(
        (Join-Path $env:LOCALAPPDATA "Stride"),
        (Join-Path ${env:ProgramFiles} "Stride"),
        "C:\Program Files\Stride"
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($root in $roots) {
        $hit = Get-ChildItem -Path $root -Recurse -Filter "Stride.GameStudio.exe" -ErrorAction SilentlyContinue |
               Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }

    throw "Could not auto-detect Game Studio. Pass -GameStudioPath pointing at Stride.GameStudio.exe."
}

function New-Shortcut {
    param([string]$LnkPath, [string]$Target, [string]$WorkDir, [string]$Icon)
    $shell = New-Object -ComObject WScript.Shell
    $sc = $shell.CreateShortcut($LnkPath)
    $sc.TargetPath       = $Target
    $sc.WorkingDirectory = $WorkDir
    $sc.IconLocation     = $Icon
    $sc.Description       = "Launch Stride Game Studio with the AkerMcp MCP plugin"
    $sc.Save()
}

# ---------------------------------------------------------------- Uninstall
if ($Uninstall) {
    $gsExe = Resolve-GameStudioExe -Path $GameStudioPath
    $gsDir = Split-Path $gsExe -Parent
    $dest  = Join-Path $gsDir "AkerMcpPlugins"

    foreach ($lnk in @($desktopLnk, $startMenuLnk)) {
        if (Test-Path $lnk) { Remove-Item $lnk -Force; Write-Host "Removed shortcut: $lnk" }
    }
    if (Test-Path $dest) {
        Remove-Item $dest -Recurse -Force
        Write-Host "Removed plugin folder: $dest"
    }
    Write-Host "Uninstalled. The official Stride shortcut is unaffected."
    return
}

# ---------------------------------------------------------------- Install
$gsExe = Resolve-GameStudioExe -Path $GameStudioPath
$gsDir = Split-Path $gsExe -Parent
Write-Host "Game Studio: $gsExe"

$adapter = Join-Path $repoRoot "plugins\stride\AkerMcp.Stride.csproj"
$hook    = Join-Path $repoRoot "plugins\stride-startuphook\AkerMcp.Stride.StartupHook.csproj"
$laun    = Join-Path $repoRoot "plugins\stride-launcher\AkerMcp.Stride.Launcher.csproj"

if (-not $NoBuild) {
    # The adapter must bind to the exact Stride assemblies this Game Studio ships.
    Write-Host "Building adapter against $gsDir ..."
    dotnet build $adapter -c $Configuration -p:StrideBin="$gsDir" --nologo
    if ($LASTEXITCODE -ne 0) { throw "Adapter build failed (exit $LASTEXITCODE)." }

    Write-Host "Building startup hook ..."
    dotnet build $hook -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Startup-hook build failed (exit $LASTEXITCODE)." }

    Write-Host "Building launcher ..."
    dotnet build $laun -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Launcher build failed (exit $LASTEXITCODE)." }
}

$adapterOut = Join-Path $repoRoot "plugins\stride\bin\$Configuration"
$hookOut    = Join-Path $repoRoot "plugins\stride-startuphook\bin\$Configuration"
$launOut    = Join-Path $repoRoot "plugins\stride-launcher\bin\$Configuration"
foreach ($p in @($adapterOut, $hookOut, $launOut)) {
    if (-not (Test-Path $p)) { throw "Build output not found: $p (run without -NoBuild?)." }
}

$dest = Join-Path $gsDir "AkerMcpPlugins"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

$copied = New-Object System.Collections.Generic.List[string]
function Copy-Plugin {
    param([string]$File)
    try {
        Copy-Item $File -Destination $dest -Force
        $copied.Add([System.IO.Path]::GetFileName($File))
    }
    catch [System.IO.IOException] {
        throw "Could not overwrite '$([System.IO.Path]::GetFileName($File))' — it is locked. Close Game Studio first, then re-run."
    }
}

# Adapter + its non-Stride dependencies. Never copy Stride.*.dll — Game Studio
# already provides those and a duplicate would shadow/conflict.
Get-ChildItem $adapterOut -Filter *.dll | Where-Object { $_.Name -notlike "Stride.*" } |
    ForEach-Object { Copy-Plugin $_.FullName }

# Startup hook.
Copy-Plugin (Join-Path $hookOut "AkerMcp.Stride.StartupHook.dll")

# Launcher (exe + its runtime config / deps).
Get-ChildItem $launOut -Filter "AkerMcp.Stride.Launcher.*" |
    Where-Object { $_.Extension -in @(".exe", ".dll", ".json") } |
    ForEach-Object { Copy-Plugin $_.FullName }

Write-Host "Deployed to $dest :"
$copied | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" }

$launcherExe = Join-Path $dest "AkerMcp.Stride.Launcher.exe"
New-Shortcut -LnkPath $desktopLnk   -Target $launcherExe -WorkDir $gsDir -Icon $gsExe
New-Shortcut -LnkPath $startMenuLnk -Target $launcherExe -WorkDir $gsDir -Icon $gsExe

Write-Host ""
Write-Host "Done. Launch Stride from the new '$($ShortcutName -replace '\.lnk$','')' shortcut"
Write-Host "(Desktop + Start Menu). Open a project and the AkerMcp server starts automatically."
Write-Host "The official Stride shortcut keeps launching Game Studio without AkerMcp."
