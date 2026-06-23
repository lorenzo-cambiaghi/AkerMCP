#!/usr/bin/env bash
# Link the sample harnesses to the canonical plugin sources.
# The plugins live under plugins/; the samples only reference them via symlinks.
set -e
cd "$(dirname "$0")"

echo "Linking sample harnesses to the canonical plugin sources..."

# --- Godot ------------------------------------------------------------------
mkdir -p samples/godot/addons
rm -rf samples/godot/addons/aker_mcp
ln -s ../../../plugins/godot samples/godot/addons/aker_mcp

# --- Unity ------------------------------------------------------------------
mkdir -p samples/unity/Assets
rm -rf samples/unity/Assets/AkerMcp
ln -s ../../../plugins/unity samples/unity/Assets/AkerMcp

echo "Done. You can now open samples/godot or samples/unity in their editors."
