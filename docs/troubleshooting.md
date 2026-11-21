# Verifying, troubleshooting and building from source

## Verifying the Connection

Once both the Unity plugin and an AI client are running, you can verify the connection:

1. **In Unity**: the AkerMcp window should show **Running** (green)
2. **In the AI client**: ask the AI to use the `inspect` tool:

```
"Inspect the scene hierarchy"
```

You should get back a tree of objects with their components:

```
Player  [Transform, Rigidbody]
  PlayerCamera  [Transform, Camera]
Enemy_1  [Transform, MeshFilter, MeshRenderer, BoxCollider, Rigidbody]
Enemy_2  [Transform, MeshFilter, MeshRenderer, BoxCollider, Rigidbody]
Ground  [Transform, MeshFilter, MeshRenderer, MeshCollider]
```

If you see this, everything is working.


## Troubleshooting

**Start with `doctor`**

`AkerMcp.Server doctor` (or `npx -y aker-mcp@latest doctor`) lists what every AI client's `game-engine` entry starts and whether it can, the editors running with the plugin, and what to do next. See [From a terminal](setup.md#from-a-terminal-setup-and-doctor).

**Connect in Unity says it could not download the server**

It fetches `AkerMcp.Server-<os>-x64.zip` of the plugin's own version from the GitHub release. Behind a proxy or offline, download that zip yourself, unpack it and run `AkerMcp.Server setup` from the folder: it installs the same server in the same place, and the window then shows the client as connected.

**On Windows the client says `spawn npx ENOENT` (or the server never starts)**

A bare `"command": "npx"` cannot be started without a shell on Windows, where npx is a `.cmd` script. Use `"command": "cmd"` with `"args": ["/c", "npx", "-y", "aker-mcp@latest"]`, or run `setup` again, which writes it that way; `doctor` flags the bare form.

**A client config looks wrong after `setup` or Connect**

The file as it was before the last change is next to it, as `<file>.aker-backup`: copy it back. `setup --uninstall` (or **Disconnect**) removes only the `game-engine` entry.

**Claude Code ignores the entry in one project**

Claude Code also keeps servers per project (`claude mcp add` without `--scope user` writes there), and inside that project its entry wins. `doctor` lists those projects; `claude mcp remove game-engine` run in the project removes its own entry.

**The AI says a tool "is not loaded in tool profile 'standard'"**

The default profile hides the authoring, verification and build tools. Start the server with `--profile full` (or `AKER_MCP_PROFILE=full`), or add just that tool with `AKER_MCP_TOOLS_INCLUDE=playtest`. See [MCP Tools](tools.md#mcp-tools).

**The server says "No engine plugin discovered"**

No editor with the AkerMcp plugin is running, or its plugin is stopped. Open **Window → AkerMcp** in Unity (or enable the plugin in Godot, or open a project in Stride's Game Studio) and check it says **Running**. Order does not matter: the server keeps looking in the background and connects on its own, without a restart.

**Unity shows DLL loading errors**

Make sure you copied *all* DLLs from the `.publish/` folder, including `System.Text.Json.dll`. Unity does not ship this library by default.

**Property not found on component**

Prefix the property with the component type name: `Rigidbody.mass` instead of just `mass`. This disambiguates when multiple components share property names.

**The first server start is slow**

`dotnet run` compiles the server on first launch. Subsequent starts are fast. You can also use `dotnet build -c Release` ahead of time, then run the compiled binary directly:

```bash
./Server/bin/Release/net8.0/AkerMcp.Server
```

**Connection drops after Unity recompiles scripts**

Domain reload in Unity tears down the plugin to safely release file locks. It starts again by itself after the recompile, unless you turned off **Start AkerMCP when Unity opens and after each recompile** under **Advanced** in the AkerMcp window; then click **Start**. The MCP server features an infinite background retry loop and will automatically detect the new instance and reconnect; you do **not** need to restart the server.

**macOS: `take_screenshot` returns "macOS denied the screen capture"**

Only happens when the OS-level fallback is used (engine adapter doesn't implement `IScreenCapture`). Open **System Settings → Privacy & Security → Screen Recording**, enable the entry for the binary running the server (typically `dotnet`), then **restart the server**; macOS caches the denial decision until the process restarts. See [macOS: Screen Recording permission](tools.md#macos-screen-recording-permission) for the full procedure.

**macOS: `take_screenshot` returns "No on-screen window found for PID"**

Only happens with the OS-level fallback. The engine's main window cannot be located by its title. Verify that `IEngineCapabilities.EngineName` in your adapter matches the actual editor window title prefix (e.g. `"Unity"` for Unity Editor). The match is case-insensitive and may be anywhere in the title.

**Unity says "Opening file failed: Access is denied"**

If you downloaded the repository as a ZIP or cloned it on Windows, Unity might complain about `.asset` or `.meta` files being read-only. To fix this:
1. Right-click the `samples\unity` folder in Windows Explorer.
2. Go to **Properties**.
3. Uncheck the **Read-only** box and click Apply (apply to all folders, subfolders, and files).
Alternatively, open Command Prompt and run: `attrib -R "samples\unity\*.*" /S /D`


---

## Advanced: Building from Source (For Developers)

If you want to modify AkerMCP or test the included Unity project, you'll need the **.NET 8.0+ SDK**.

### Step 1: clone and build
```bash
git clone https://github.com/lorenzo-cambiaghi/AkerMCP.git
cd AkerMCP
dotnet build -c Release
dotnet publish Shared/AkerMcp.Shared.csproj -c Release -o .publish
```

### Step 2: Unity plugin setup
If you are modifying the source code and want to push changes to your own Unity project:
1. Copy this repo's `plugins/unity` folder into your own Unity project as `Assets/AkerMcp`.
2. Create `Assets/AkerMcp/Editor/Plugins/` and copy all `.dll` files from `.publish/` and `Client/bin/Release/netstandard2.1/`. The folder must sit under `Editor/`: there Unity imports the DLLs for the editor only, so player builds don't carry them. If an older install has them in `Assets/AkerMcp/Plugins/`, move them (with their `.meta` files) rather than copying a second set: Unity refuses two assemblies with the same name.
3. Copy the Unity Roslyn compiler (`Microsoft.CodeAnalysis.dll`, `Microsoft.CodeAnalysis.CSharp.dll`, `System.Reflection.Metadata.dll`) from your Unity Editor installation (`.../Editor/Data/MonoBleedingEdge/lib/mono/4.5/`) into the same `Editor/Plugins/` folder.

If you just want to run the included sample, run `./setup-samples.sh` (or `setup-samples.bat` on Windows) once to link the plugin into `samples/unity`, then `./copy-dlls.sh` (or `copy-dlls.bat`) to build and copy all dependencies. Open `samples/unity` in Unity.

### Packaging a Release
On Windows, `.\publish-release.ps1 -Version v1.2.3` does the whole release in one command: builds the packages (Unity must be closed), tags the commit, creates the [GitHub Release](https://github.com/lorenzo-cambiaghi/AkerMCP/releases) and uploads the five artifacts via the GitHub API (auth via `GITHUB_TOKEN` or the stored git credential). Use `-DryRun` to preview, `-SkipBuild` to reuse existing `Build/` output.

The six release artifacts are: `AkerMCP.unitypackage` (Unity plugin), `AkerMcp.Godot-addon.zip` (Godot addon, with the AkerMcp core source it builds against under `aker_mcp/core/`; extract into your project's `addons/` and reference it per [1b. Godot Setup](setup.md#1b-godot-setup)), `AkerMcp.Stride-source.zip` (Stride adapter **source**, with the startup hook, the launcher, the `Shared/` and `Client/` sources it builds from, and `install-stride-wrapper.ps1`; build it against your Game Studio per [1c. Stride Setup](setup.md#1c-stride-setup); it is not a prebuilt binary because it links your specific Stride editor assemblies), and the three standalone server builds (`AkerMcp.Server-{win,osx,linux}-x64.zip`).

Alternatively, run `build-package.bat` (Windows) or `./build-package.sh` (Mac/Linux) to only produce the artifacts in the local `Build/` folder (gitignored), then upload them as release assets manually. Binaries are distributed via Releases, not committed to the repository.
