# aker-mcp

![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)
![Platform](https://img.shields.io/badge/platform-Unity%20%7C%20Godot-lightgrey.svg)
![MCP](https://img.shields.io/badge/mcp-compatible-green.svg)

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

aker-mcp lets AI assistants inspect, query, and manipulate game scenes through a small set of reflection-based tools and **Roslyn-powered dynamic scripting** that work across **Unity**, **Godot**, and any .NET-compatible engine — without writing engine-specific tool classes.

---

## In 30 Seconds

Traditional MCP integrations for game engines ship 100+ hand-written tools — one per operation, one per component type. Every engine update breaks them.

aker-mcp replaces all of that with **13 generic tools** powered by runtime reflection and **Roslyn**. A single `set_property` tool can modify *any* property on *any* object in *any* engine, while the `execute` tool enables complex procedural generation via C# scripts. The engine-specific adapter provides the necessary layer for interacting directly with the engine's API.

```
AI: "Set the player's position to (10, 0, 5)"

→ set_property {"object_path": "/Player", "property_path": "position", "value": {"x":10,"y":0,"z":5}}
← Property 'position' set successfully on /Player
```

No custom tool class needed. No code generation. Just reflection.

---

## Features

- **13 Generic Reflection-Based Tools**: Operate on any object or component without custom tool definitions.
- **Roslyn-Powered Dynamic Execution**: Send arbitrary C# scripts via the `execute` tool to perform complex procedural tasks or bulk operations directly within the Unity Editor.
- **MessagePack IPC Protocol**: High-performance, low-latency binary communication between the standalone MCP Server and the engine plugin.
- **Robust Type System**: Serializes and deserializes Unity-specific structs (`Vector3`, `Color`, `Bounds`) seamlessly.
- **Engine-Agnostic Core**: Shared .NET Standard 2.1 core makes it easy to port to Godot or other engines by writing a simple adapter.

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
- [AI Integration Rules](#ai-integration-rules)
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

The easiest way to integrate aker-mcp is by using the provided scripts. These scripts build the project, publish the dependencies, and copy all necessary DLLs (including Roslyn) directly into your Unity project's `Plugins` folder.

If you're using the included test project (`UnityTestProject/`):

```bash
# macOS / Linux
# Set UNITY_EDITOR_PATH to your Unity.app/Contents path to include Roslyn DLLs
export UNITY_EDITOR_PATH=/Applications/Unity/Hub/Editor/2022.3.0f1/Unity.app/Contents
./copy-dlls.sh

# Windows
# Set UNITY_EDITOR_PATH to your Unity Editor\Data folder to include Roslyn DLLs
set UNITY_EDITOR_PATH=C:\Program Files\Unity\Hub\Editor\2022.3.0f1\Editor\Data
copy-dlls.bat
```

> **Note on Roslyn:** The `execute` tool requires Roslyn DLLs to compile C# at runtime. The scripts attempt to locate these automatically in common Unity installation paths. If they fail, set the `UNITY_EDITOR_PATH` variable manually as shown above.

> **Manual Installation:**
> If you want to add aker-mcp to your **own** Unity project manually, create a folder like `Assets/Plugins/AkerMcp` and copy the following:
> 1. All DLLs generated in `.publish/` after running `dotnet publish Shared/AkerMcp.Shared.csproj -c Release -o .publish`.
> 2. `AkerMcp.Client.dll` from `Client/bin/Release/netstandard2.1/`.
> 3. The Roslyn DLLs (`Microsoft.CodeAnalysis.dll`, `Microsoft.CodeAnalysis.CSharp.dll`, `Microsoft.CodeAnalysis.Scripting.dll`, `Microsoft.CodeAnalysis.CSharp.Scripting.dll`, and `System.Reflection.Metadata.dll`) from your Unity Editor's `MonoBleedingEdge/lib/mono/4.5/` directory.
> 4. The adapter C# scripts from `UnityTestProject/Assets/AkerMcp/` into your project's `Assets/` folder.

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

### Google Antigravity

Antigravity reads `mcp_config.json` from its user-data directory:

- Windows: `%USERPROFILE%\.gemini\antigravity\mcp_config.json`
- macOS: `~/.gemini/antigravity/mcp_config.json`
- Linux: `~/.gemini/antigravity/mcp_config.json`

Add an entry under `mcpServers`:

```json
{
  "mcpServers": {
    "game-engine": {
      "command": "dotnet",
      "args": ["run", "--project", "C:/path/to/aker-mcp/Server"],
      "type": "stdio"
    }
  }
}
```

Restart Antigravity. The new tools appear automatically.

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
| `execute` | Run arbitrary C# code in the engine context (Roslyn) |

### Dynamic Code Execution (`execute`)

The `execute` tool runs arbitrary C# code inside the Unity Editor using Roslyn (`Microsoft.CodeAnalysis.CSharp.Scripting`). This is the most powerful tool — it can do anything the Unity Editor API allows.

**What it enables:**

- Procedural scene generation (spawn 100 objects in a grid, create terrain, etc.)
- Bulk property modifications across many objects
- Asset manipulation (create materials, import textures, modify prefabs)
- Complex queries that go beyond what `query` supports
- Editor automation (menu items, build pipeline, custom importers)
- Anything you can do in a Unity Editor script

**Built-in globals** available in your code:

| Global | Type | Description |
|--------|------|-------------|
| `selectedObject` | `GameObject?` | Currently selected GameObject in the editor |
| `Find(name)` | `GameObject?` | Shortcut for `GameObject.Find(name)` |
| `FindAll<T>()` | `T[]` | Find all objects of type T |
| `Create(name)` | `GameObject` | Create a new empty GameObject |
| `Log(message)` | `void` | Log to the Unity console |

**Imported namespaces** (no `using` needed): `System`, `System.Collections.Generic`, `System.Linq`, `UnityEngine`, `UnityEditor`

**Examples:**

```csharp
// Create a grid of cubes
for (int x = 0; x < 5; x++)
    for (int z = 0; z < 5; z++) {
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = $"Cube_{x}_{z}";
        cube.transform.position = new Vector3(x * 2, 0, z * 2);
    }
```

```csharp
// Set all enemies to red
var enemies = FindAll<Renderer>()
    .Where(r => r.gameObject.name.StartsWith("Enemy"))
    .ToArray();
foreach (var r in enemies)
    r.material.color = Color.red;
return $"Colored {enemies.Length} enemies";
```

```csharp
// Return scene stats
var objects = FindAll<GameObject>();
var types = objects
    .SelectMany(go => go.GetComponents<Component>())
    .Where(c => c != null)
    .GroupBy(c => c.GetType().Name)
    .Select(g => $"{g.Key}: {g.Count()}")
    .ToArray();
return string.Join("\n", types);
```

> **Note:** The script state persists between calls — variables defined in one `execute` call are available in the next. The evaluator runs on Unity's main thread with full Editor API access.

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
│   ├── MainThreadDispatcherBase.cs     Thread-safe queue with TCS pattern
│   └── ClientConfiguration.cs          Client-side settings
└── UnityTestProject/                    Unity 6 test project
    └── Assets/AkerMcp/                  Unity adapter implementation
        ├── UnitySceneGraph.cs           Scene traversal and node creation
        ├── UnitySceneNode.cs            Reflection wrapper for GameObjects
        ├── UnityTypeRegistration.cs     MessagePack types and aliases
        └── Editor/                      Editor-only tooling
            ├── DynamicEvaluator.cs      Roslyn-powered C# execution engine
            ├── McpEditorWindow.cs       Unity Editor UI for MCP server
            ├── UnityCompilationSupport.cs Script compilation tools
            ├── UnityEditorContext.cs    Active selection and console logs
            ├── UnityMainThreadDispatcher.cs Unity main thread marshalling
            └── UnityMcpPlugin.cs        Plugin entry point
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

## AI Integration Rules

To get the best results, give your AI client a rules file that teaches it *how* to use aker-mcp. Copy the template below into your Unity project root — the AI will learn when to use `inspect` vs `execute`, how to format property paths, and when to check for compilation errors.

### Where to put the rules file

| Platform | File | Scope |
|----------|------|-------|
| Claude Code | `CLAUDE.md` (project root) | Per-project |
| Antigravity | `AGENTS.md` (project root) | Per-project |
| Cursor | `.cursor/rules/aker-mcp.md` | Per-project |
| Cross-tool | `AGENTS.md` (project root) | Works with most clients |

> **Recommended:** Use `AGENTS.md` in your Unity project root. It's the most widely supported convention.

### Rules template

Copy everything inside the block below into your rules file:

---

<details>
<summary><strong>Click to expand the full rules template</strong></summary>

#### aker-mcp — AI Integration Rules

You have access to a Unity (or Godot) game engine via the `game-engine` MCP server. This gives you 13 tools to inspect, query, modify, and script the active scene — all from the editor.

#### Available Tools (quick reference)

| Tool | Use when... |
|------|-------------|
| `inspect` | You need to see what's on a GameObject — components, properties, children |
| `get_property` | You know the exact path and want a single value |
| `set_property` | You want to change one property with undo support |
| `call_method` | You need to invoke a method (e.g. `SetActive`, `AddForce`) |
| `query` | You need to find objects by type, name pattern, or tag |
| `create` | You need to add a new GameObject to the scene |
| `delete` | You need to remove a GameObject (destructive, has undo) |
| `select` | You want to highlight an object in Unity's Hierarchy/Inspector |
| `get_selection` | You want to know what the user currently has selected |
| `refresh_scripts` | You just wrote or modified a `.cs` file and need to trigger recompilation |
| `get_compile_errors` | You need to check if scripts compiled successfully |
| `get_console_logs` | You need to read runtime errors, warnings, or debug output |
| `execute` | You need to run arbitrary C# code (procedural generation, bulk ops, complex logic) |

#### Core Workflow: Inspect → Modify → Verify

Always follow this pattern:

1. **Inspect first.** Before modifying anything, call `inspect` to see the object's components, properties, and current values. Never guess.
2. **Modify.** Use `set_property` for single changes, `execute` for complex operations.
3. **Verify.** Call `get_property` or `inspect` again to confirm the change took effect. Check `get_console_logs` if something seems wrong.

```
Bad:  set_property "/Player" "mass" 5        ← "mass" might not resolve (it's on Rigidbody)
Good: inspect "/Player" → see "Rigidbody.mass" exists → set_property "/Player" "Rigidbody.mass" 5
```

#### Property Path Syntax

Properties use **dot-notation** resolved via reflection:

```
position                → Transform.position (Transform is searched first)
position.x              → float
Rigidbody.mass          → targets the Rigidbody component specifically
Rigidbody.useGravity    → bool
MeshRenderer.material.color → Color
```

**Rules:**
- Transform properties (`position`, `rotation`, `localScale`, `eulerAngles`) don't need a prefix
- Other components need the type prefix: `Rigidbody.mass`, `Camera.fieldOfView`, `Light.intensity`
- Nested properties work: `MeshRenderer.material.color.r`
- Array indexing works: `mesh.vertices[0]`

**Structs are passed as JSON objects:**
```json
{"x": 1.0, "y": 2.0, "z": 3.0}           // Vector3
{"r": 1.0, "g": 0.0, "b": 0.0, "a": 1.0} // Color
```

#### When to Use `execute` vs Other Tools

| Scenario | Use |
|----------|-----|
| Change one property | `set_property` |
| Read one value | `get_property` |
| Find objects | `query` |
| Create one object | `create` |
| Modify 10+ objects in a loop | `execute` |
| Generate procedural content | `execute` |
| Access Editor API (AssetDatabase, Undo groups, etc.) | `execute` |
| Complex conditional logic | `execute` |
| Create materials, shaders, ScriptableObjects | `execute` |

#### Writing `execute` Scripts

**Available globals** (no setup needed):

```csharp
selectedObject              // Currently selected GameObject (or null)
Find("Player")              // GameObject.Find shortcut
FindAll<Rigidbody>()        // Find all components of a type
Create("MyObject")          // Create empty GameObject
Log("message")              // Debug.Log shortcut
```

**Pre-imported namespaces**: `System`, `System.Collections.Generic`, `System.Linq`, `UnityEngine`, `UnityEditor`

**State persists between calls.** Variables you define in one `execute` are available in the next:

```csharp
// Call 1
var player = Find("Player");
// Call 2
return player.transform.position;  // still accessible
```

**Return values** are sent back to you. Always `return` a meaningful result:

```csharp
// Good — returns useful info
var count = FindAll<Rigidbody>().Length;
return $"Found {count} rigidbodies";

// Bad — no feedback
FindAll<Rigidbody>();  // returns null, you won't know the result
```

**Timeout**: Default is 5 seconds. Pass `timeout_ms` for longer operations.

#### After Writing or Modifying C# Scripts

Whenever you create or edit a `.cs` file in the Unity project:

1. Call `refresh_scripts` — this forces Unity to recompile
2. Call `get_compile_errors` — check for errors
3. If errors exist, fix them and repeat

```
→ refresh_scripts {}
← Recompilation requested. Result: FAILED
  === ERRORS (1) ===
  Assets/Scripts/Player.cs(15,9): error CS1002: ; expected

→ (fix the file)

→ refresh_scripts {}
← Result: SUCCESS. No errors or warnings.
```

**Never assume a script change compiled successfully.** Always verify.

#### Scene Navigation

**Paths** use forward slashes from the scene root:

```
/Player
/Player/PlayerCamera
/Environment/Trees/Oak_01
```

**To find objects** when you don't know the path:
- `query {"name_pattern": "Player*"}` — glob search
- `query {"type_filter": "Camera"}` — by component type
- `query {"tag": "Enemy"}` — by tag

**To explore the full scene:**
- Read the `scene://hierarchy` resource — returns the complete tree with components
- Or call `inspect` on root objects

#### Anti-Patterns

| Don't | Do instead |
|-------|------------|
| Guess property names | `inspect` the object first |
| Modify without inspecting | Inspect → modify → verify |
| Use `execute` for one property change | `set_property` (supports undo) |
| Ignore compile errors after writing scripts | `refresh_scripts` → `get_compile_errors` |
| Assume paths are case-insensitive | They're case-sensitive on the engine side |
| Create complex objects one property at a time | Use `execute` with a single script |
| Forget to `return` values in `execute` | Always return a string describing what happened |

#### Error Recovery

If a tool call fails:

1. **Read the error message** — it usually tells you exactly what's wrong
2. **Inspect the target** — the object may not exist, or the property name may be different
3. **Check the console** — `get_console_logs {"level_filter": "error"}` shows runtime errors
4. **For compile errors** — `get_compile_errors` shows the exact file, line, and column

</details>

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

[Apache 2.0](LICENSE)
