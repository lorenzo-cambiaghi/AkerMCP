# aker-mcp

A generic, engine-agnostic [Model Context Protocol](https://modelcontextprotocol.io/) (MCP) bridge for game engines.

```text
                                         Aker (Egyptian: ꜣkr) was an
                                         ancient Egyptian earth god,
                                         often depicted as two lions
    /\__/\             /\__/\            seated back-to-back facing
  (  -.-  )          (  -.-  )           opposite horizons. Named
  >       <          >       <           Sef and Duau (Yesterday and
   /      )          (      \            Today), they guarded the
   \      /          \      /            passage of the sun through the
    | /    \        /    \ |             underworld, opening the gates
    | |    )|      |(    | |             for its safe transit.
  (___)  _//        \\_  (___)
       _\_/          \_/_                In this architecture, Aker
                                         serves as the unyielding
                                         bridge: one face speaking
                                         JSON-RPC to the LLM, the
                                         other manipulating the
                                         engine's main thread via IPC.
```

aker-mcp lets AI assistants inspect, query, and manipulate game scenes through a small set of reflection-based tools that work across **Unity**, **Godot**, and any .NET-compatible engine — without writing engine-specific tool classes.

---

## In 30 Seconds

Traditional MCP integrations for game engines ship 100+ hand-written tools — one per operation, one per component type. Every engine update breaks them.

aker-mcp replaces all of that with **13 generic tools** powered by runtime reflection. A single `set_property` tool can modify *any* property on *any* object in *any* engine. The engine-specific adapter is under 500 lines.

```
AI: "Set the player's position to (10, 0, 5)"

→ set_property {"object_path": "/Player", "property_path": "position", "value": {"x":10,"y":0,"z":5}}
← Property 'position' set successfully on /Player
```

No custom tool class needed. No code generation. Just reflection.

---

## Table of Contents

- [Prerequisites](#prerequisites)
- [Installation](#installation)
- [Unity Plugin Setup](#unity-plugin-setup)
- [Connecting an AI Client](#connecting-an-ai-client)
  - [Claude Code (CLI)](#claude-code-cli)
  - [Claude Desktop](#claude-desktop)
  - [Cursor](#cursor)
  - [Windsurf](#windsurf)
  - [VS Code + Copilot](#vs-code--copilot)
- [Verifying the Connection](#verifying-the-connection)
- [MCP Tools](#mcp-tools)
- [MCP Resources](#mcp-resources)
- [Type System](#type-system)
- [Example Session](#example-session)
- [Architecture](#architecture)
- [Writing an Engine Adapter](#writing-an-engine-adapter)
- [Troubleshooting](#troubleshooting)
- [License](#license)

---

## Prerequisites

You need two things installed before starting:

| Requirement | Version | Check | Install |
|-------------|---------|-------|---------|
| **.NET SDK** | 8.0+ | `dotnet --version` | [dotnet.microsoft.com/download](https://dotnet.microsoft.com/download) |
| **Unity** | 2021.3+ | Unity Hub | [unity.com/download](https://unity.com/download) |

> **Note:** aker-mcp also works with Godot 4.x (.NET), but this guide focuses on Unity. See [Writing an Engine Adapter](#writing-an-engine-adapter) for Godot.

---

## Installation

### Step 1 — Clone the repository

```bash
git clone https://github.com/lorenzo-cambiaghi/aker-mcp.git
cd aker-mcp
```

### Step 2 — Build

```bash
dotnet build -c Release
```

You should see:

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### Step 3 — Publish dependencies

This gathers all required DLLs (including MessagePack) into a single folder:

```bash
dotnet publish Shared/AkerMcp.Shared.csproj -c Release -o .publish
```

That's it. The server is ready to run. Now set up the Unity side.

---

## Unity Plugin Setup

### Step 1 — Copy DLLs into your Unity project

If you're using the included test project (`UnityTestProject/`):

```bash
./copy-dlls.sh
```

> If you want to add aker-mcp to your **own** Unity project, copy the DLLs manually:
>
> ```bash
> DEST=path/to/YourProject/Assets/Plugins/AkerMcp
> mkdir -p $DEST
>
> cp .publish/AkerMcp.Shared.dll                        $DEST/
> cp Client/bin/Release/netstandard2.1/AkerMcp.Client.dll $DEST/
> cp .publish/MessagePack.dll                            $DEST/
> cp .publish/MessagePack.Annotations.dll                $DEST/
> cp .publish/Microsoft.Bcl.AsyncInterfaces.dll          $DEST/
> cp .publish/Microsoft.NET.StringTools.dll              $DEST/
> cp .publish/System.Buffers.dll                         $DEST/
> cp .publish/System.Collections.Immutable.dll           $DEST/
> cp .publish/System.Memory.dll                          $DEST/
> cp .publish/System.Runtime.CompilerServices.Unsafe.dll $DEST/
> cp .publish/System.Text.Encodings.Web.dll              $DEST/
> cp .publish/System.Text.Json.dll                       $DEST/
> cp .publish/System.Threading.Tasks.Extensions.dll      $DEST/
> ```
>
> Then copy the adapter scripts from `UnityTestProject/Assets/AkerMcp/` into your project's `Assets/` folder.

### Step 2 — Open the Unity project

Open `UnityTestProject/` (or your own project) in **Unity Hub**.

### Step 3 — Create a test scene (optional)

Menu bar: **AkerMcp → Setup Test Scene**

This creates a scene with a Player (Rigidbody + Camera), three Enemies, a Ground plane, lights, and props — enough to test all tools.

### Step 4 — Start the plugin

Menu bar: **Window → AkerMcp**

Click **Start AkerMcp Plugin**.

You'll see a green **Running** status and a pipe name like `aker-mcp-unity-12345`. The plugin is now waiting for the MCP server to connect.

> **Tip:** The plugin must be running *before* you start the server. The server discovers it automatically via a lock file in your system's temp directory.

---

## Connecting an AI Client

The MCP server is a standalone .NET process that the AI client launches. You configure it once, and it connects to the Unity plugin automatically.

> **Important:** Make sure the Unity plugin is running (green status in the AkerMcp window) before using any tools from the AI client.

### Claude Code (CLI)

Run this once to add the server to your Claude Code configuration:

```bash
claude mcp add game-engine -- dotnet run --project /absolute/path/to/aker-mcp/Server
```

Or add it manually to your project's `.claude/settings.json`:

```json
{
  "mcpServers": {
    "game-engine": {
      "command": "dotnet",
      "args": ["run", "--project", "/absolute/path/to/aker-mcp/Server"]
    }
  }
}
```

Verify it's registered:

```bash
claude mcp list
```

### Claude Desktop

Open **Settings → Developer → Edit Config** and add:

```json
{
  "mcpServers": {
    "game-engine": {
      "command": "dotnet",
      "args": ["run", "--project", "/absolute/path/to/aker-mcp/Server"]
    }
  }
}
```

Config file location:
- **macOS:** `~/Library/Application Support/Claude/claude_desktop_config.json`
- **Windows:** `%APPDATA%\Claude\claude_desktop_config.json`

Restart Claude Desktop after saving.

### Cursor

Open **Settings → MCP** and click **+ Add new MCP server**, then choose **command** type:

```json
{
  "mcpServers": {
    "game-engine": {
      "command": "dotnet",
      "args": ["run", "--project", "/absolute/path/to/aker-mcp/Server"]
    }
  }
}
```

Or add it directly to `.cursor/mcp.json` in your project root.

### Windsurf

Open **Settings → MCP** and add:

```json
{
  "mcpServers": {
    "game-engine": {
      "command": "dotnet",
      "args": ["run", "--project", "/absolute/path/to/aker-mcp/Server"]
    }
  }
}
```

### VS Code + Copilot

Add to your `.vscode/settings.json` or use the **MCP: Add Server** command:

```json
{
  "mcp": {
    "servers": {
      "game-engine": {
        "command": "dotnet",
        "args": ["run", "--project", "/absolute/path/to/aker-mcp/Server"]
      }
    }
  }
}
```

> **Windows users:** Replace `/absolute/path/to/aker-mcp/Server` with the full Windows path, e.g. `C:\\Users\\you\\aker-mcp\\Server`. Use double backslashes in JSON.

---

## Verifying the Connection

Once both the Unity plugin and an AI client are running, you can verify the connection:

1. **In Unity** — the AkerMcp window should show **Running** (green)
2. **In the AI client** — ask the AI to use the `inspect` tool:

```
"Inspect the scene hierarchy"
```

You should get back a tree of GameObjects with their components:

```
Player  [Transform, Rigidbody]
  PlayerCamera  [Transform, Camera]
Enemy_1  [Transform, MeshFilter, MeshRenderer, BoxCollider, Rigidbody]
Enemy_2  [Transform, MeshFilter, MeshRenderer, BoxCollider, Rigidbody]
Ground  [Transform, MeshFilter, MeshRenderer, MeshCollider]
```

If you see this, everything is working.

---

## MCP Tools

### Scene Manipulation

| Tool | Description |
|------|-------------|
| `inspect` | Return components, properties, methods, and children of a scene object or type |
| `get_property` | Read a property via dot-notation path (e.g. `transform.position.x`) |
| `set_property` | Write a property — supports primitives, structs, arrays, nested objects |
| `call_method` | Invoke a method on a scene object or a static class method |
| `query` | Find objects by type name, name pattern, tag, or property values |
| `create` | Add a new object to the scene with optional initial properties |
| `delete` | Remove an object from the scene (supports undo) |
| `select` | Select a GameObject in the editor — highlights it in the Hierarchy and Inspector |
| `get_selection` | Get the currently selected object with its components, properties, and children |

### Development Workflow

| Tool | Description |
|------|-------------|
| `refresh_scripts` | Force script recompilation and return errors/warnings immediately |
| `get_compile_errors` | Retrieve compilation errors with file path, line, and column |
| `get_console_logs` | Read engine console entries with level and text filtering |
| `execute` | Run arbitrary C# code in the engine context |

### How property paths work

Properties are accessed via **dot-notation paths** resolved at runtime through reflection:

```
transform.position.x          → float
Rigidbody.mass                → float (targets a specific component)
MeshRenderer.material.color   → Color
```

When a property name is ambiguous (e.g. `enabled` exists on multiple components), prefix it with the component type: `Rigidbody.enabled`, `MeshRenderer.enabled`.

---

## MCP Resources

| URI | Description |
|-----|-------------|
| `scene://hierarchy` | Full scene tree with components listed per object |
| `project://info` | Engine name/version, project path, active scene |
| `editor://logs` | Recent console entries |
| `editor://compile_status` | Compilation status, error/warning counts |
| `engine://types` | Registered engine type names |

---

## Type System

The serializer converts between JSON and .NET types via reflection. It handles:

- **Primitives** — `int`, `float`, `double`, `bool`, `string`, `enum`
- **Structs** — any value type, constructed from JSON via field/property matching
- **Arrays** — `T[]` from JSON arrays
- **Lists** — `List<T>` from JSON arrays
- **Dictionaries** — `Dictionary<string, T>` from JSON objects
- **Nested types** — recursive resolution (e.g. `Bounds` containing `Vector3` fields)
- **Nullable** — automatic unwrap

The Unity adapter registers optimized converters for:

```
Vector2  Vector3  Vector4  Vector2Int  Vector3Int
Quaternion  Color  Color32  Rect  RectInt
Bounds  BoundsInt  LayerMask
```

JSON examples:

```json
{ "x": 1.0, "y": 2.0, "z": 3.0 }                                              // Vector3
{ "r": 0.5, "g": 0.0, "b": 1.0, "a": 1.0 }                                    // Color
{ "center": { "x": 0, "y": 0, "z": 0 }, "size": { "x": 10, "y": 10, "z": 10 }} // Bounds
[{ "x": 0, "y": 0, "z": 0 }, { "x": 1, "y": 1, "z": 1 }]                      // Vector3[]
```

---

## Example Session

```
→ inspect {"target": "/Player"}
← {
    "typeName": "Rigidbody",
    "path": "/Player",
    "components": [
      {"name": "Transform", "enabled": true},
      {"name": "Rigidbody", "enabled": true}
    ],
    "properties": [
      {"name": "position", "type": "Vector3", "value": {"x":0,"y":1,"z":0}},
      {"name": "Rigidbody.mass", "type": "float", "value": 1.0},
      ...
    ],
    "childNames": ["PlayerCamera"]
  }

→ select {"object_path": "/Player/PlayerCamera"}
← {"selected": true, "path": "/Player/PlayerCamera", "name": "PlayerCamera",
    "components": [{"name":"Transform"}, {"name":"Camera"}]}

→ set_property {
    "object_path": "/Player",
    "property_path": "position",
    "value": {"x": 10, "y": 0, "z": 5}
  }
← Property 'position' set successfully on /Player

→ query {"type_filter": "Camera"}
← [{"path": "/Player/PlayerCamera", "type": "Camera", "name": "PlayerCamera"}]

→ refresh_scripts {}
← Recompilation requested. Status: idle
  Last compile: 14:32:05
  Result: SUCCESS
  No errors or warnings.

→ get_console_logs {"level_filter": "error", "count": 10}
← (No matching log entries)
```

---

## Architecture

```
                  ┌──────────────────────┐
                  │  LLM (Claude, etc.)  │
                  └──────────┬───────────┘
                             │ JSON-RPC 2.0 / stdio
                  ┌──────────▼───────────┐
                  │    aker-mcp Server   │   .NET 8 console process
                  │   13 MCP tools       │
                  │    5 MCP resources   │
                  └──────────┬───────────┘
                             │ Named Pipe + MessagePack
                  ┌──────────▼───────────┐
                  │   Engine Plugin      │   runs inside Unity / Godot
                  │   ISceneGraph impl   │
                  └──────────────────────┘
```

| Project | Target | Description |
|---------|--------|-------------|
| `AkerMcp.Shared` | netstandard2.1 | Protocol models, engine abstractions, reflection engine, serialization, IPC |
| `AkerMcp.Server` | net8.0 | MCP server — JSON-RPC over stdio, routes tool calls to the engine plugin |
| `AkerMcp.Client` | netstandard2.1 | Plugin base class — runs inside the engine, handles IPC and main-thread dispatch |

### How it works

1. The **engine plugin** starts a named-pipe server and writes a lock file to the system temp directory.
2. The **MCP server** scans for lock files, connects to the pipe, and begins forwarding tool calls.
3. The **LLM** sends JSON-RPC requests over stdio. The server forwards them to the engine plugin via MessagePack IPC and returns results as JSON.
4. The **engine plugin** dispatches requests to the main thread, executes reflection-based operations through `ISceneGraph`/`ISceneNode`, and returns results.

Property paths like `transform.position.x` are resolved at runtime by `PropertyPathResolver`, which walks the object graph via cached reflection metadata. Struct value-type propagation is handled automatically.

### Project structure

```
aker-mcp/
├── AkerMcp.sln
├── Shared/                              AkerMcp.Shared (netstandard2.1)
│   ├── Protocol/                        JSON-RPC and MCP message models
│   ├── Abstraction/                     Engine-agnostic interfaces
│   ├── Reflection/                      PropertyPathResolver, inspector, cache
│   ├── Serialization/                   GenericSerializer, TypeRegistry
│   └── Ipc/                             Named pipe channel, binary framing
├── Server/                              AkerMcp.Server (net8.0 console app)
│   ├── McpServer.cs                     JSON-RPC dispatcher, MCP lifecycle
│   ├── ToolRegistry.cs                  13 generic tool definitions
│   ├── ResourceRegistry.cs             5 resource definitions
│   ├── EngineConnection.cs             IPC client to engine plugin
│   └── StdioTransport.cs              stdin/stdout transport
├── Client/                              AkerMcp.Client (netstandard2.1)
│   ├── EnginePluginBase.cs             Abstract base for adapters
│   ├── IpcRequestHandler.cs            Request routing and execution
│   ├── PluginDiscovery.cs              Lock-file based auto-discovery
│   └── MainThreadDispatcherBase.cs     Thread-safe queue with TCS pattern
└── UnityTestProject/                    Unity 6 test project
    └── Assets/AkerMcp/                  Unity adapter (~500 LOC)
```

---

## Writing an Engine Adapter

To support a new engine (e.g. Godot), subclass `EnginePluginBase` and implement the required interfaces:

```csharp
public class MyEnginePlugin : EnginePluginBase
{
    // Required
    protected override ISceneGraph CreateSceneGraph() => new MySceneGraph();
    protected override IEngineCapabilities CreateCapabilities() => new MyCapabilities();
    protected override IMainThreadDispatcher CreateDispatcher() => new MyDispatcher();

    // Optional
    protected override IEditorContext? CreateEditorContext() => new MyEditorContext();
    protected override IAssetManager? CreateAssetManager() => null;
    protected override ICompilationSupport? CreateCompilationSupport() => new MyCompilationSupport();

    protected override void Log(string message) { /* ... */ }
    protected override void LogError(string message) { /* ... */ }
}
```

| Interface | Purpose | Required |
|-----------|---------|:--------:|
| `ISceneGraph` | Scene tree traversal, create/delete, query | Yes |
| `ISceneNode` | Property get/set, method invocation, component listing | Yes |
| `IEngineCapabilities` | Type resolution, engine metadata | Yes |
| `IMainThreadDispatcher` | Marshal actions to the engine's main thread | Yes |
| `IEditorContext` | Selection, scene management, console logs | No |
| `IAssetManager` | Asset search, load, save, delete | No |
| `ICompilationSupport` | Script recompilation, error retrieval | No |

Register custom type converters for engine-specific structs:

```csharp
TypeRegistry.Instance.RegisterCustomSerializer<Vector3>(
    v => new Dictionary<string, object?> { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z },
    d => new Vector3(F(d, "x"), F(d, "y"), F(d, "z"))
);
```

---

## Troubleshooting

**The server says "No engine plugin discovered"**

The Unity plugin must be started *before* the MCP server. Open **Window → AkerMcp** in Unity and click **Start** first.

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

Domain reload in Unity tears down the plugin. Re-click **Start** in the AkerMcp window after a recompile, then restart the server.

---

## License

MIT
