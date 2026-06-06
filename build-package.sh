#!/bin/bash
set -e

echo "======================================================="
echo "AkerMCP Unity Package Builder (macOS/Linux)"
echo "======================================================="
echo ""

echo "[1/3] Ensuring DLLs are compiled and up to date..."
./copy-dlls.sh

echo ""
echo "[2/3] Locating Unity Editor..."
if [ -z "$UNITY_EDITOR_PATH" ]; then
    # Try common macOS default path
    if [ -d "/Applications/Unity/Hub/Editor" ]; then
        for dir in /Applications/Unity/Hub/Editor/*/; do
            if [ -f "${dir}Unity.app/Contents/MacOS/Unity" ]; then
                UNITY_EXE="${dir}Unity.app/Contents/MacOS/Unity"
                # Keep the last one found (usually highest version)
            fi
        done
    fi
else
    # Assume UNITY_EDITOR_PATH points to Unity.app/Contents
    UNITY_EXE="$UNITY_EDITOR_PATH/MacOS/Unity"
fi

if [ -z "$UNITY_EXE" ] || [ ! -f "$UNITY_EXE" ]; then
    echo "ERROR: Could not find Unity executable."
    echo "Please set UNITY_EDITOR_PATH to your Unity.app/Contents folder."
    exit 1
fi
echo "Found Unity at: $UNITY_EXE"

echo ""
echo "[3/3] Exporting AkerMCP.unitypackage..."
echo "(NOTE: If Unity is currently open with the test project, this step will fail or skip silently. Please close Unity first!)"
echo ""

PROJECT_PATH="$(pwd)/UnityTestProject"
OUTPUT_PATH="$(pwd)/AkerMCP.unitypackage"

"$UNITY_EXE" -quit -batchmode -projectPath "$PROJECT_PATH" -exportPackage "Assets/AkerMcp" "$OUTPUT_PATH" -logFile -

if [ ! -f "$OUTPUT_PATH" ]; then
    echo "ERROR: Package was not generated. Ensure Unity is closed before running this script!"
    exit 1
fi

echo ""
echo "SUCCESS!"
echo "The package has been created at: $OUTPUT_PATH"
echo "You can upload this file to GitHub Releases."
