#!/bin/bash
set -e

echo "======================================================="
echo "AkerMCP Unity Package Builder (macOS/Linux)"
echo "======================================================="
echo ""

echo "[1/4] Ensuring sample plugin symlink and DLLs are up to date..."
# The .unitypackage is exported from samples/unity, whose Assets/AkerMcp is a
# symlink to the canonical plugin (plugins/unity/AkerMcp). Make sure it exists.
./setup-samples.sh
./copy-dlls.sh

echo ""
echo "[2/4] Locating Unity Editor..."
if [ -n "$UNITY_EDITOR_PATH" ]; then
    # Assume UNITY_EDITOR_PATH points to Unity.app/Contents
    UNITY_EXE="$UNITY_EDITOR_PATH/MacOS/Unity"
else
    HUB="/Applications/Unity/Hub/Editor"
    # Prefer the editor that matches the project's version (ProjectVersion.txt),
    # otherwise a newer editor would silently upgrade the project on import.
    PROJ_VER=$(grep '^m_EditorVersion:' "$(pwd)/samples/unity/ProjectSettings/ProjectVersion.txt" | awk '{print $2}' | tr -d '\r')
    if [ -n "$PROJ_VER" ] && [ -f "$HUB/$PROJ_VER/Unity.app/Contents/MacOS/Unity" ]; then
        UNITY_EXE="$HUB/$PROJ_VER/Unity.app/Contents/MacOS/Unity"
        echo "Using project-matched editor $PROJ_VER."
    else
        echo "WARNING: Editor $PROJ_VER (from ProjectVersion.txt) is not installed."
        echo "         Falling back to the newest installed editor - this may upgrade the project."
        echo "         Set UNITY_EDITOR_PATH to pin a specific editor."
        if [ -d "$HUB" ]; then
            for dir in "$HUB"/*/; do
                if [ -f "${dir}Unity.app/Contents/MacOS/Unity" ]; then
                    UNITY_EXE="${dir}Unity.app/Contents/MacOS/Unity"
                fi
            done
        fi
    fi
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

PROJECT_PATH="$(pwd)/samples/unity"
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

echo "  - Godot addon (aker_mcp)"
rm -f "$BUILD_DIR/AkerMcp.Godot-addon.zip"
(cd plugins/godot && zip -r "$BUILD_DIR/AkerMcp.Godot-addon.zip" aker_mcp > /dev/null)

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
echo "  - Build/AkerMcp.Godot-addon.zip"
echo "  - Build/AkerMcp.Server-win-x64.tar.gz"
echo "  - Build/AkerMcp.Server-osx-x64.tar.gz"
echo "  - Build/AkerMcp.Server-linux-x64.tar.gz"
echo "You can upload these files to GitHub Releases."
