@echo off
setlocal

echo =======================================================
echo AkerMCP Unity Package Builder
echo =======================================================

echo.
echo [1/3] Ensuring DLLs are compiled and up to date...
call copy-dlls.bat
if errorlevel 1 goto :error

echo.
echo [2/3] Locating Unity Editor...
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
echo [3/3] Exporting AkerMCP.unitypackage...
echo (NOTE: If Unity is currently open with the test project, this step will fail or skip silently. Please close Unity first!)
echo.

"%UNITY_EXE%" -quit -batchmode -projectPath "%~dp0UnityTestProject" -exportPackage "Assets/AkerMcp" "%~dp0AkerMCP.unitypackage" -logFile -
if errorlevel 1 goto :error

if not exist "%~dp0AkerMCP.unitypackage" (
    echo ERROR: Package was not generated. Ensure Unity is closed before running this script!
    goto :error
)

echo.
echo SUCCESS! 
echo The package has been created at: %~dp0AkerMCP.unitypackage
echo You can upload this file to GitHub Releases.
goto :end

:error
echo.
echo Build failed.
exit /b 1

:end
endlocal
