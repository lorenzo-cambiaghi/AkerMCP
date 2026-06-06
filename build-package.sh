#!/bin/bash
set -e

echo "======================================================="
echo "AkerMCP Unity Package Builder (macOS/Linux)"
echo "======================================================="
echo ""

echo "[1/4] Ensuring DLLs are compiled and up to date..."
./copy-dlls.sh

echo ""
echo "[2/4] Locating Unity Editor..."
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
echo "[3/4] Exporting AkerMCP.unitypackage..."
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
echo "[4/4] Publishing standalone Server binaries..."
BUILD_DIR="$(pwd)/Build"
mkdir -p "$BUILD_DIR"
mv "$OUTPUT_PATH" "$BUILD_DIR/"

echo "  - Windows (win-x64)"
dotnet publish Server/AkerMcp.Server.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$BUILD_DIR/Server-win-x64" --nologo > /dev/null
(cd "$BUILD_DIR/Server-win-x64" && tar -czf "$BUILD_DIR/AkerMcp.Server-win-x64.tar.gz" .)

echo "  - macOS (osx-x64)"
dotnet publish Server/AkerMcp.Server.csproj -c Release -r osx-x64 --self-contained true -p:PublishSingleFile=true -o "$BUILD_DIR/Server-osx-x64" --nologo > /dev/null
(cd "$BUILD_DIR/Server-osx-x64" && tar -czf "$BUILD_DIR/AkerMcp.Server-osx-x64.tar.gz" .)

echo "  - Linux (linux-x64)"
dotnet publish Server/AkerMcp.Server.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o "$BUILD_DIR/Server-linux-x64" --nologo > /dev/null
(cd "$BUILD_DIR/Server-linux-x64" && tar -czf "$BUILD_DIR/AkerMcp.Server-linux-x64.tar.gz" .)

# Clean up intermediate folders
rm -rf "$BUILD_DIR/Server-win-x64"
rm -rf "$BUILD_DIR/Server-osx-x64"
rm -rf "$BUILD_DIR/Server-linux-x64"

echo ""
echo "SUCCESS!"
echo "All packages have been created in the Build/ directory:"
echo "  - Build/AkerMCP.unitypackage"
echo "  - Build/AkerMcp.Server-win-x64.tar.gz"
echo "  - Build/AkerMcp.Server-osx-x64.tar.gz"
echo "  - Build/AkerMcp.Server-linux-x64.tar.gz"
echo "You can upload these files to GitHub Releases."
