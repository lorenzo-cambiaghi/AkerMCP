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

# NuGet's global packages folder, which is where the Stride Launcher installs Game
# Studio (Stride's NugetStore takes SettingsUtility.GetGlobalPackagesFolder). Same order
# NuGet uses: NUGET_PACKAGES, then a globalPackagesFolder from NuGet.config (dotnet
# reports the effective one), then the default under the user profile.
function Get-NuGetPackagesRoot {
    if ($env:NUGET_PACKAGES) { return $env:NUGET_PACKAGES }
    try {
        $line = & dotnet nuget locals global-packages --list 2>$null | Select-Object -First 1
        if ($line -match 'global-packages:\s*(.+?)\s*$') { return $Matches[1].TrimEnd('\') }
    }
    catch { }
    return (Join-Path $env:USERPROFILE ".nuget\packages")
}

# Sort key of a package version folder ("4.2.0.2381", "4.3.0.1-beta"): the numbers
# first, and a release above its own prereleases.
function Get-VersionKey {
    param([string]$Name)
    if ($Name -match '^(\d+(?:\.\d+){1,3})(-.*)?$') {
        return [pscustomobject]@{ Version = [version]$Matches[1]; IsRelease = -not $Matches[2] }
    }
    return [pscustomobject]@{ Version = [version]"0.0"; IsRelease = $false }
}

# Every Game Studio found on this machine, newest first: Launcher installs in the NuGet
# cache (<root>\stride.gamestudio\<version>\lib\<tfm>\Stride.GameStudio.exe; the Launcher
# also looks under tools\), then older install roots.
function Find-GameStudioCandidates {
    $found = New-Object System.Collections.Generic.List[string]

    $pkgDir = Join-Path (Get-NuGetPackagesRoot) "stride.gamestudio"
    if (Test-Path $pkgDir) {
        $versions = Get-ChildItem $pkgDir -Directory -ErrorAction SilentlyContinue |
            Sort-Object @{ Expression = { (Get-VersionKey $_.Name).Version }; Descending = $true },
                        @{ Expression = { (Get-VersionKey $_.Name).IsRelease }; Descending = $true }
        foreach ($v in $versions) {
            foreach ($top in @("lib", "tools")) {
                $frameworks = Join-Path $v.FullName $top
                if (-not (Test-Path $frameworks)) { continue }
                foreach ($fw in Get-ChildItem $frameworks -Directory -ErrorAction SilentlyContinue) {
                    $exe = Join-Path $fw.FullName "Stride.GameStudio.exe"
                    if (Test-Path $exe) { $found.Add($exe) }
                }
            }
        }
    }

    $roots = @(
        (Join-Path $env:LOCALAPPDATA "Stride"),
        $(if ($env:ProgramData) { Join-Path $env:ProgramData "Stride" }),
        $(if (${env:ProgramFiles}) { Join-Path ${env:ProgramFiles} "Stride" })
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique
    foreach ($root in $roots) {
        Get-ChildItem -Path $root -Recurse -Filter "Stride.GameStudio.exe" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            ForEach-Object { if (-not $found.Contains($_.FullName)) { $found.Add($_.FullName) } }
    }
    return $found.ToArray()
}

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

    $candidates = @(Find-GameStudioCandidates)
    if ($candidates.Count -gt 0) { return $candidates[0] }
    throw "Could not auto-detect Game Studio (looked in the NuGet packages folder '$(Get-NuGetPackagesRoot)' and the Stride install folders). Pass -GameStudioPath pointing at Stride.GameStudio.exe."
}

# The target framework the adapter and launcher must be built for: the runtime this
# Game Studio runs on (stock 4.2: .NET 8, 4.3+: .NET 10). Built for the wrong one, the
# adapter either fails to compile (CS1705) or fails to load.
function Get-StrideTfm {
    param([string]$GameStudioExe)
    $config = [IO.Path]::ChangeExtension($GameStudioExe, ".runtimeconfig.json")
    if (Test-Path $config) {
        $tfm = (Get-Content $config -Raw | ConvertFrom-Json).runtimeOptions.tfm   # "net8.0", "net10.0"
        if ($tfm -match '^net\d+\.\d+$') { return "$tfm-windows" }                # Game Studio is WPF
    }
    # No runtimeconfig: a package's lib\<tfm> folder names it (net8.0-windows7.0).
    $folder = Split-Path (Split-Path $GameStudioExe -Parent) -Leaf
    if ($folder -match '^(net\d+\.\d+)') { return "$($Matches[1])-windows" }
    Write-Warning "Could not tell which .NET this Game Studio runs on; assuming net10.0-windows."
    return "net10.0-windows"
}

# A folder holding every Stride assembly the adapter references, for -p:StrideBin.
# A source build has them all next to Stride.GameStudio.exe. A Launcher install does
# not: its package folder holds the exe, Roslyn and NuGet, and each Stride assembly
# lives in its own package of the same name and version (Game Studio restores them on
# its first start and resolves them at run time). Those are gathered into
# plugins\stride\obj\StrideRefs\<version>-<tfm>. The list is read from the adapter's .csproj,
# so it cannot drift from what the build needs.
function Get-StrideReferenceDir {
    param([string]$GameStudioExe, [string]$AdapterProject)

    $gsDir = Split-Path $GameStudioExe -Parent
    if (Test-Path (Join-Path $gsDir "Stride.Core.Assets.Editor.dll")) { return $gsDir }

    $tfmDir     = Split-Path $gsDir -Leaf                              # net10.0-windows7.0
    $versionDir = Split-Path (Split-Path $gsDir -Parent) -Parent       # ...\stride.gamestudio\<version>
    $version    = Split-Path $versionDir -Leaf
    $packageDir = Split-Path $versionDir -Parent
    if ((Split-Path $packageDir -Leaf) -ne "stride.gamestudio") {
        throw "The Stride assemblies are not next to '$GameStudioExe', and it is not a Launcher (NuGet) install either. Pass -GameStudioPath of a complete Game Studio."
    }
    $packagesRoot = Split-Path $packageDir -Parent

    $names = ([xml](Get-Content $AdapterProject -Raw)).Project.ItemGroup.Reference |
        Where-Object { $_ -and $_.HintPath -like '*$(StrideBin)*' } |
        ForEach-Object { $_.Include }

    $refDir = Join-Path $repoRoot "plugins\stride\obj\StrideRefs\$version-$tfmDir"
    New-Item -ItemType Directory -Force -Path $refDir | Out-Null

    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($name in $names) {
        $source = Join-Path $gsDir "$name.dll"                         # Roslyn ships with Game Studio
        if (-not (Test-Path $source)) {
            $source = $null
            $lib = Join-Path $packagesRoot "$($name.ToLowerInvariant())\$version\lib"
            if (Test-Path $lib) {
                # The package's framework folder closest to Game Studio's own.
                $source = Get-ChildItem $lib -Directory |
                    Sort-Object { if ($_.Name -eq $tfmDir) { 0 } elseif ($tfmDir.StartsWith($_.Name)) { 1 } else { 2 } } |
                    ForEach-Object { Join-Path $_.FullName "$name.dll" } |
                    Where-Object { Test-Path $_ } | Select-Object -First 1
            }
        }
        if (-not $source) { $missing.Add($name); continue }

        # Skip an identical copy: the compiler server may still map the one from the last build.
        $target = Join-Path $refDir "$name.dll"
        $src = Get-Item $source
        $dst = Get-Item $target -ErrorAction SilentlyContinue
        if (-not $dst -or $dst.Length -ne $src.Length -or $dst.LastWriteTimeUtc -ne $src.LastWriteTimeUtc) {
            Copy-Item $source -Destination $target -Force
        }
    }

    if ($missing.Count -gt 0) {
        Write-Warning ("Not found in the NuGet cache for Stride $version" + ": " + ($missing -join ", ") +
            ". If the build fails on them, start Game Studio once from the Stride Launcher (it restores its packages), then re-run.")
    }
    return $refDir
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
    # Shortcuts first: removing them needs nothing else, and resolving Game Studio can
    # fail (moved, upgraded, uninstalled), which used to abort here and leave them behind.
    foreach ($lnk in @($desktopLnk, $startMenuLnk)) {
        if (Test-Path $lnk) { Remove-Item $lnk -Force; Write-Host "Removed shortcut: $lnk" }
    }

    # Without -GameStudioPath, clean every Game Studio that has the plugin: the one
    # installed into may no longer be the newest.
    $targets = @()
    if ($GameStudioPath) {
        try { $targets = @(Resolve-GameStudioExe -Path $GameStudioPath) }
        catch { Write-Warning "$($_.Exception.Message) The plugin folder was not removed." }
    }
    else {
        $targets = @(Find-GameStudioCandidates | Where-Object { Test-Path (Join-Path (Split-Path $_ -Parent) "AkerMcpPlugins") })
        if ($targets.Count -eq 0) { Write-Host "No Game Studio with an AkerMcpPlugins folder was found." }
    }

    foreach ($gsExe in $targets) {
        $dest = Join-Path (Split-Path $gsExe -Parent) "AkerMcpPlugins"
        if (-not (Test-Path $dest)) { continue }
        try {
            Remove-Item $dest -Recurse -Force -ErrorAction Stop
            Write-Host "Removed plugin folder: $dest"
        }
        catch {
            Write-Warning ("Could not remove '$dest' completely: a file in it is in use. Close Game Studio " +
                "(and give any 'dotnet build' it started a minute to exit), then run -Uninstall again.")
        }
    }

    # The launcher's shadow copies of the startup hook (one still in use stays).
    $shadow = Join-Path ([IO.Path]::GetTempPath()) "AkerMcp\stride-hook"
    if (Test-Path $shadow) { Remove-Item $shadow -Recurse -Force -ErrorAction SilentlyContinue }

    Write-Host "Uninstalled. The official Stride shortcut is unaffected."
    return
}

# ---------------------------------------------------------------- Install
$gsExe = Resolve-GameStudioExe -Path $GameStudioPath
$gsDir = Split-Path $gsExe -Parent
$tfm   = Get-StrideTfm -GameStudioExe $gsExe
Write-Host "Game Studio: $gsExe ($tfm)"

$adapter = Join-Path $repoRoot "plugins\stride\AkerMcp.Stride.csproj"
$hook    = Join-Path $repoRoot "plugins\stride-startuphook\AkerMcp.Stride.StartupHook.csproj"
$laun    = Join-Path $repoRoot "plugins\stride-launcher\AkerMcp.Stride.Launcher.csproj"

if (-not $NoBuild) {
    # The adapter must bind to the exact Stride assemblies this Game Studio ships.
    $strideBin = Get-StrideReferenceDir -GameStudioExe $gsExe -AdapterProject $adapter
    Write-Host "Building adapter against $strideBin ..."
    dotnet build $adapter -c $Configuration -p:StrideBin="$strideBin" -p:StrideTfm=$tfm --nologo
    if ($LASTEXITCODE -ne 0) { throw "Adapter build failed (exit $LASTEXITCODE)." }

    Write-Host "Building startup hook ..."
    dotnet build $hook -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Startup-hook build failed (exit $LASTEXITCODE)." }

    # Same runtime as Game Studio: a machine with only Stride's .NET can still start it.
    Write-Host "Building launcher ..."
    dotnet build $laun -c $Configuration -p:StrideTfm=$tfm --nologo
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
        throw "Could not overwrite '$([System.IO.Path]::GetFileName($File))' - it is locked. Close Game Studio first, then re-run."
    }
}

# Adapter + its non-Stride dependencies. Never copy Stride.*.dll - Game Studio
# already provides those and a duplicate would shadow/conflict.
Get-ChildItem $adapterOut -Filter *.dll | Where-Object { $_.Name -notlike "Stride.*" } |
    ForEach-Object { Copy-Plugin $_.FullName }

# Startup hook. The launcher runs a shadow copy of it from %TEMP%, so this file is
# never held by Game Studio's child processes.
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
