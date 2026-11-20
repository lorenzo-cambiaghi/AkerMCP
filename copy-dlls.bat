@echo off
setlocal
REM Run from the repo root regardless of the caller's working directory
cd /d "%~dp0"

REM Under an Editor folder: Unity imports a DLL there for the editor only. They used to sit in
REM plugins\unity\Plugins, where a DLL with no importer settings is imported for EVERY platform, and
REM each player build shipped ~11 MB of editor-only assemblies (Roslyn, System.Text.Json, MessagePack).
set DEST=plugins\unity\Editor\Plugins
set OLD=plugins\unity\Plugins

REM An older run left the DLLs in %OLD%: move them, .meta included so the GUIDs survive. Copying fresh
REM ones next to them instead would leave two copies of every assembly, which Unity refuses to load.
if exist "%OLD%\*.dll" (
    if not exist "%DEST%" mkdir "%DEST%"
    echo Moving the DLLs of a previous run from %OLD% to %DEST%...
    move /Y "%OLD%\*.dll" "%DEST%\" >nul
    if exist "%OLD%\*.dll.meta" move /Y "%OLD%\*.dll.meta" "%DEST%\" >nul
)
if exist "%OLD%" rmdir "%OLD%" 2>nul
if not exist "%OLD%" if exist "%OLD%.meta" del /Q "%OLD%.meta"

if not exist "%DEST%" mkdir "%DEST%"

REM execute compiles with CSharpCompilation directly and no longer needs Roslyn's scripting layer.
for %%F in (Microsoft.CodeAnalysis.Scripting Microsoft.CodeAnalysis.CSharp.Scripting) do (
    if exist "%DEST%\%%F.dll" del /Q "%DEST%\%%F.dll"
    if exist "%DEST%\%%F.dll.meta" del /Q "%DEST%\%%F.dll.meta"
)

echo Building...
dotnet clean -c Release --nologo
dotnet build -c Release --nologo
if errorlevel 1 goto :error

echo Publishing dependencies...
dotnet publish Shared\AkerMcp.Shared.csproj -c Release -o .publish --nologo
if errorlevel 1 goto :error

echo Copying DLLs to %DEST%...
copy /Y .publish\AkerMcp.Shared.dll                        "%DEST%\" >nul
copy /Y Client\bin\Release\netstandard2.1\AkerMcp.Client.dll "%DEST%\" >nul
copy /Y .publish\MessagePack.dll                            "%DEST%\" >nul
copy /Y .publish\MessagePack.Annotations.dll                "%DEST%\" >nul
copy /Y .publish\Microsoft.Bcl.AsyncInterfaces.dll          "%DEST%\" >nul
copy /Y .publish\Microsoft.NET.StringTools.dll              "%DEST%\" >nul
copy /Y .publish\System.Buffers.dll                         "%DEST%\" >nul
copy /Y .publish\System.Collections.Immutable.dll           "%DEST%\" >nul
copy /Y .publish\System.Memory.dll                          "%DEST%\" >nul
copy /Y .publish\System.Runtime.CompilerServices.Unsafe.dll "%DEST%\" >nul
copy /Y .publish\System.Text.Encodings.Web.dll              "%DEST%\" >nul
copy /Y .publish\System.Text.Json.dll                       "%DEST%\" >nul
copy /Y .publish\System.Threading.Tasks.Extensions.dll      "%DEST%\" >nul

REM Roslyn DLLs - sourced from Unity's Mono installation
REM Set UNITY_EDITOR_PATH if your Unity is not in the default location
if not defined UNITY_EDITOR_PATH (
    REM Try common Windows default paths
    if exist "C:\Program Files\Unity\Hub\Editor" (
        for /d %%D in ("C:\Program Files\Unity\Hub\Editor\*") do (
            set UNITY_EDITOR_PATH=%%D\Editor\Data
        )
    )
)

if defined UNITY_EDITOR_PATH (
    set UNITY_MONO=%UNITY_EDITOR_PATH%\MonoBleedingEdge\lib\mono\4.5
) else (
    set UNITY_MONO=
)

if defined UNITY_MONO if exist "%UNITY_MONO%" (
    echo Copying Roslyn DLLs from Unity Mono...
    copy /Y "%UNITY_MONO%\Microsoft.CodeAnalysis.dll"                 "%DEST%\" >nul
    copy /Y "%UNITY_MONO%\Microsoft.CodeAnalysis.CSharp.dll"          "%DEST%\" >nul
    copy /Y "%UNITY_MONO%\System.Reflection.Metadata.dll"             "%DEST%\" >nul
) else (
    echo WARNING: Unity Mono path not found.
    echo   Set UNITY_EDITOR_PATH to your Unity Editor\Data folder to include Roslyn DLLs.
    echo   Example: set UNITY_EDITOR_PATH=C:\Program Files\Unity\Hub\Editor\6000.2.12f1\Editor\Data
    echo   The 'execute' tool will not work without them.
)

echo Done.
goto :end

:error
echo Build failed.
exit /b 1

:end
endlocal
