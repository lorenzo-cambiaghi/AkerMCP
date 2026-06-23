@echo off
setlocal
REM Run from the repo root regardless of the caller's working directory.
cd /d "%~dp0"

echo Linking sample harnesses to the canonical plugin sources...
echo (The plugins live under plugins\; the samples only reference them via junctions.)
echo.

REM --- Godot ----------------------------------------------------------------
if exist "samples\godot\addons\aker_mcp" rmdir "samples\godot\addons\aker_mcp"
if not exist "samples\godot\addons" mkdir "samples\godot\addons"
mklink /J "samples\godot\addons\aker_mcp" "plugins\godot"
if errorlevel 1 goto :error

REM --- Unity ----------------------------------------------------------------
if exist "samples\unity\Assets\AkerMcp" rmdir "samples\unity\Assets\AkerMcp"
mklink /J "samples\unity\Assets\AkerMcp" "plugins\unity"
if errorlevel 1 goto :error

echo.
echo Done. You can now open samples\godot or samples\unity in their editors.
goto :end

:error
echo.
echo Failed to create a junction. Run this script from a normal (non-elevated is fine) prompt.
exit /b 1

:end
endlocal
