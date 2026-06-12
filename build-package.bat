@echo off
setlocal
REM Run from the repo root regardless of the caller's working directory
cd /d "%~dp0"

echo =======================================================
echo AkerMCP Unity Package Builder
echo =======================================================

echo.
echo [1/4] Ensuring DLLs are compiled and up to date...
call "%~dp0copy-dlls.bat"
if errorlevel 1 goto :error

echo.
echo [2/4] Locating Unity Editor...
if not defined UNITY_EDITOR_PATH (
    if exist "C:\Program Files\Unity\Hub\Editor" (
        for /d %%D in ("C:\Program Files\Unity\Hub\Editor\*") do (
            set UNITY_EDITOR_PATH=%%D\Editor\Data
            set UNITY_EXE=%%D\Editor\Unity.exe
        )
    )
) else (
    REM If UNITY_EDITOR_PATH is manually set to Editor\Data, we need to go up one level to find Unity.exe
    set UNITY_EXE=%UNITY_EDITOR_PATH%\..\Unity.exe
)

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

"%UNITY_EXE%" -quit -batchmode -projectPath "%~dp0UnityTestProject" -exportPackage "Assets/AkerMcp" "%~dp0AkerMCP.unitypackage" -logFile -
if errorlevel 1 goto :error

if not exist "%~dp0AkerMCP.unitypackage" (
    echo ERROR: Package was not generated. Ensure Unity is closed before running this script!
    goto :error
)

echo.
echo [4/4] Publishing standalone Server binaries...
if not exist "%~dp0Build" mkdir "%~dp0Build"
move /Y "%~dp0AkerMCP.unitypackage" "%~dp0Build\" >nul

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
