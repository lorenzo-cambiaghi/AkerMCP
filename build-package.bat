@echo off
setlocal
REM Run from the repo root regardless of the caller's working directory
cd /d "%~dp0"

echo =======================================================
echo AkerMCP Unity Package Builder
echo =======================================================

echo.
echo [1/4] Ensuring sample plugin junction and DLLs are up to date...
REM The .unitypackage is exported from samples\unity, whose Assets\AkerMcp is a
REM junction to the canonical plugin (plugins\unity\AkerMcp). Make sure it exists.
call "%~dp0setup-samples.bat"
if errorlevel 1 goto :error
call "%~dp0copy-dlls.bat"
if errorlevel 1 goto :error

echo.
echo [2/4] Locating Unity Editor...
if defined UNITY_EDITOR_PATH (
    REM If UNITY_EDITOR_PATH is manually set to Editor\Data, go up one level for Unity.exe
    set UNITY_EXE=%UNITY_EDITOR_PATH%\..\Unity.exe
    goto :have_unity
)

set "HUB=C:\Program Files\Unity\Hub\Editor"

REM Prefer the editor that matches the project's version (ProjectVersion.txt),
REM otherwise a newer editor would silently upgrade the project on import.
set PROJ_VER=
for /f "tokens=2" %%v in ('findstr /b "m_EditorVersion:" "%~dp0samples\unity\ProjectSettings\ProjectVersion.txt"') do set PROJ_VER=%%v

if exist "%HUB%\%PROJ_VER%\Editor\Unity.exe" (
    set "UNITY_EDITOR_PATH=%HUB%\%PROJ_VER%\Editor\Data"
    set "UNITY_EXE=%HUB%\%PROJ_VER%\Editor\Unity.exe"
    echo Using project-matched editor %PROJ_VER%.
    goto :have_unity
)

echo WARNING: Editor %PROJ_VER% (from ProjectVersion.txt) is not installed.
echo          Falling back to the newest installed editor - this may upgrade the project.
echo          Set UNITY_EDITOR_PATH to pin a specific editor.
if exist "%HUB%" (
    for /d %%D in ("%HUB%\*") do (
        if exist "%%D\Editor\Unity.exe" (
            set "UNITY_EDITOR_PATH=%%D\Editor\Data"
            set "UNITY_EXE=%%D\Editor\Unity.exe"
        )
    )
)

:have_unity
if not exist "%UNITY_EXE%" (
    echo ERROR: Could not find Unity.exe.
    echo Please set UNITY_EDITOR_PATH to your Unity Editor\Data folder.
    goto :error
)
echo Found Unity at: %UNITY_EXE%

echo.
echo [3/4] Exporting AkerMCP.unitypackage...
echo (NOTE: If Unity is currently open with the test project, this step will fail or skip silently. Please close Unity first!)
echo.

"%UNITY_EXE%" -quit -batchmode -projectPath "%~dp0samples\unity" -exportPackage "Assets/AkerMcp" "%~dp0AkerMCP.unitypackage" -logFile -
if errorlevel 1 goto :error

if not exist "%~dp0AkerMCP.unitypackage" (
    echo ERROR: Package was not generated. Ensure Unity is closed before running this script!
    goto :error
)

echo.
echo [4/4] Publishing standalone Server binaries...
if not exist "%~dp0Build" mkdir "%~dp0Build"
move /Y "%~dp0AkerMCP.unitypackage" "%~dp0Build\" >nul

echo   - Godot addon (aker_mcp)
REM The canonical source is now flat (plugins\godot\*). The distributed addon
REM must still be a top-level "aker_mcp\" folder, so stage a copy under that name.
if exist "%~dp0Build\_godot_stage" rmdir /s /q "%~dp0Build\_godot_stage"
mkdir "%~dp0Build\_godot_stage\aker_mcp"
xcopy "%~dp0plugins\godot\*" "%~dp0Build\_godot_stage\aker_mcp\" /E /I /Y /Q >nul
if errorlevel 1 goto :error
tar -a -c -f "%~dp0Build\AkerMcp.Godot-addon.zip" --exclude=*/.godot/* -C "%~dp0Build\_godot_stage" aker_mcp
if errorlevel 1 goto :error
rmdir /s /q "%~dp0Build\_godot_stage"

echo   - Stride adapter source (build against your Game Studio; see README "1c. Stride Setup")
tar -a -c -f "%~dp0Build\AkerMcp.Stride-source.zip" --exclude=*/bin/* --exclude=*/obj/* -C "%~dp0plugins" stride
if errorlevel 1 goto :error

echo   - Windows (win-x64)
dotnet publish Server\AkerMcp.Server.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%~dp0Build\Server-win-x64" --nologo >nul
tar -a -c -f "%~dp0Build\AkerMcp.Server-win-x64.zip" -C "%~dp0Build\Server-win-x64" .

echo   - macOS (osx-x64)
dotnet publish Server\AkerMcp.Server.csproj -c Release -r osx-x64 --self-contained true -p:PublishSingleFile=true -o "%~dp0Build\Server-osx-x64" --nologo >nul
tar -a -c -f "%~dp0Build\AkerMcp.Server-osx-x64.zip" -C "%~dp0Build\Server-osx-x64" .

echo   - Linux (linux-x64)
dotnet publish Server\AkerMcp.Server.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o "%~dp0Build\Server-linux-x64" --nologo >nul
tar -a -c -f "%~dp0Build\AkerMcp.Server-linux-x64.zip" -C "%~dp0Build\Server-linux-x64" .

REM Clean up intermediate folders
rmdir /s /q "%~dp0Build\Server-win-x64"
rmdir /s /q "%~dp0Build\Server-osx-x64"
rmdir /s /q "%~dp0Build\Server-linux-x64"

echo.
echo SUCCESS! 
echo All packages have been created in the Build/ directory:
echo   - Build/AkerMCP.unitypackage
echo   - Build/AkerMcp.Godot-addon.zip
echo   - Build/AkerMcp.Stride-source.zip
echo   - Build/AkerMcp.Server-win-x64.zip
echo   - Build/AkerMcp.Server-osx-x64.zip
echo   - Build/AkerMcp.Server-linux-x64.zip
echo You can upload these files to GitHub Releases.
goto :end

:error
echo.
echo Build failed.
exit /b 1

:end
endlocal
