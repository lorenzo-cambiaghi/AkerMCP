#!/bin/bash
set -e

DEST="UnityTestProject/Assets/Plugins/AkerMcp"
mkdir -p "$DEST"

echo "Building..."
dotnet build -c Release --nologo -q

echo "Publishing dependencies..."
dotnet publish Shared/AkerMcp.Shared.csproj -c Release -o .publish --nologo -q

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

echo "Done. $(ls "$DEST"/*.dll | wc -l | tr -d ' ') DLLs copied."
