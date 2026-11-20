#!/bin/bash
set -e

# Under an Editor folder: Unity imports a DLL there for the editor only. They used to sit in
# plugins/unity/Plugins, where a DLL with no importer settings is imported for EVERY platform, and
# each player build shipped ~11 MB of editor-only assemblies (Roslyn, System.Text.Json, MessagePack).
DEST="plugins/unity/Editor/Plugins"
OLD="plugins/unity/Plugins"

# An older run left the DLLs in $OLD: move them, .meta included so the GUIDs survive. Copying fresh
# ones next to them instead would leave two copies of every assembly, which Unity refuses to load.
if ls "$OLD"/*.dll >/dev/null 2>&1; then
    mkdir -p "$DEST"
    echo "Moving the DLLs of a previous run from $OLD to $DEST..."
    mv -f "$OLD"/*.dll "$DEST/"
    if ls "$OLD"/*.dll.meta >/dev/null 2>&1; then mv -f "$OLD"/*.dll.meta "$DEST/"; fi
fi
if [ -d "$OLD" ]; then rmdir "$OLD" 2>/dev/null || true; fi
if [ ! -d "$OLD" ]; then rm -f "$OLD.meta"; fi

mkdir -p "$DEST"

# execute compiles with CSharpCompilation directly and no longer needs Roslyn's scripting layer.
rm -f "$DEST/Microsoft.CodeAnalysis.Scripting.dll" "$DEST/Microsoft.CodeAnalysis.Scripting.dll.meta" \
      "$DEST/Microsoft.CodeAnalysis.CSharp.Scripting.dll" "$DEST/Microsoft.CodeAnalysis.CSharp.Scripting.dll.meta"

echo "Building..."
dotnet clean -c Release --nologo
dotnet build -c Release --nologo

echo "Publishing dependencies..."
dotnet publish Shared/AkerMcp.Shared.csproj -c Release -o .publish --nologo

echo "Copying DLLs to $DEST..."
cp .publish/AkerMcp.Shared.dll                        "$DEST/"
cp Client/bin/Release/netstandard2.1/AkerMcp.Client.dll "$DEST/"
cp .publish/MessagePack.dll                            "$DEST/"
cp .publish/MessagePack.Annotations.dll                "$DEST/"
cp .publish/Microsoft.Bcl.AsyncInterfaces.dll          "$DEST/"
cp .publish/Microsoft.NET.StringTools.dll              "$DEST/"
cp .publish/System.Buffers.dll                         "$DEST/"
cp .publish/System.Collections.Immutable.dll           "$DEST/"
cp .publish/System.Memory.dll                          "$DEST/"
cp .publish/System.Runtime.CompilerServices.Unsafe.dll "$DEST/"
cp .publish/System.Text.Encodings.Web.dll              "$DEST/"
cp .publish/System.Text.Json.dll                       "$DEST/"
cp .publish/System.Threading.Tasks.Extensions.dll      "$DEST/"

# Roslyn (for dynamic C# execution via the 'execute' tool)
# These are sourced from Unity's own Mono installation
UNITY_MONO="${UNITY_EDITOR_PATH:-/Applications/Unity/Hub/Editor/6000.2.12f1/Unity.app/Contents}/MonoBleedingEdge/lib/mono/4.5"
if [ -d "$UNITY_MONO" ]; then
    echo "Copying Roslyn DLLs from Unity Mono..."
    cp "$UNITY_MONO/Microsoft.CodeAnalysis.dll"                 "$DEST/"
    cp "$UNITY_MONO/Microsoft.CodeAnalysis.CSharp.dll"          "$DEST/"
    cp "$UNITY_MONO/System.Reflection.Metadata.dll"             "$DEST/"
else
    echo "WARNING: Unity Mono path not found at $UNITY_MONO"
    echo "  Set UNITY_EDITOR_PATH to your Unity.app/Contents path to include Roslyn DLLs."
    echo "  The 'execute' tool will not work without them."
fi

echo "Done. $(ls "$DEST"/*.dll | wc -l | tr -d ' ') DLLs copied."
