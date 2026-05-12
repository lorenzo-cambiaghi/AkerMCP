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
aker-mcp lets AI assistants inspect, query, and manipulate game scenes through a small set of reflection-based tools that work across **Unity**, **Godot**, and any .NET-compatible engine — without writing engine-specific tool classes.

---

## Overview

Traditional MCP integrations for game engines implement hundreds of hand-written tools, one per operation. Every new component type or API change requires new code.

aker-mcp replaces that with **13 generic tools** powered by runtime reflection and property path resolution. A single `set_property` tool can modify any property on any object in any engine. The engine-specific adapter layer is typically under 500 lines.

### Key Features

- **Engine-agnostic core** — shared protocol, reflection, and serialization layers
- **Reflection-based property access** — dot-notation paths like `transform.position.x`
- **Automatic struct handling** — Vector3, Color, Quaternion, Bounds, and custom types
- **Array and collection support** — get/set elements in arrays, lists, and dictionaries
- **Script compilation control** — trigger recompilation and retrieve errors with file/line info
- **Console log access** — read engine logs with level and text filtering
- **MessagePack IPC** — binary serialization for fast server-to-engine communication
- **Auto-discovery** — server finds running engine plugins via lock files

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

The system is split into three .NET projects:

| Project | Target | Description |
|---------|--------|-------------|
| `AkerMcp.Shared` | netstandard2.1 | Protocol models, engine abstractions, reflection engine, serialization, IPC |
| `AkerMcp.Server` | net8.0 | MCP server — JSON-RPC over stdio, routes tool calls to the engine plugin |
| `AkerMcp.Client` | netstandard2.1 | Plugin base class — runs inside the engine, handles IPC and main-thread dispatch |

The `netstandard2.1` target ensures compatibility with Unity 2021+ and Godot (.NET).

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

### MCP Resources

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

Engine adapters register custom converters for domain-specific types. The Unity adapter includes converters for:

```
Vector2  Vector3  Vector4  Vector2Int  Vector3Int
Quaternion  Color  Color32  Rect  RectInt
Bounds  BoundsInt  LayerMask
```

JSON format examples:

```json
{ "x": 1.0, "y": 2.0, "z": 3.0 }
{ "r": 0.5, "g": 0.0, "b": 1.0, "a": 1.0 }
{ "center": { "x": 0, "y": 0, "z": 0 }, "size": { "x": 10, "y": 10, "z": 10 } }
[{ "x": 0, "y": 0, "z": 0 }, { "x": 1, "y": 1, "z": 1 }]
```

---

## Getting Started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- Unity 2021.3+ or Godot 4.x (.NET)

### Build

```bash
git clone https://github.com/youruser/aker-mcp.git
cd aker-mcp
dotnet build -c Release
```

### Unity Setup

1. **Publish dependencies**

   ```bash
   dotnet publish Shared/AkerMcp.Shared.csproj -c Release -o .publish
   ```

2. **Copy DLLs into the Unity project**

   ```bash
   DEST=UnityTestProject/Assets/Plugins/AkerMcp
   mkdir -p $DEST

   cp .publish/AkerMcp.Shared.dll                        $DEST/
   cp Client/bin/Release/netstandard2.1/AkerMcp.Client.dll $DEST/
   cp .publish/MessagePack.dll                            $DEST/
   cp .publish/MessagePack.Annotations.dll                $DEST/
   cp .publish/Microsoft.Bcl.AsyncInterfaces.dll          $DEST/
   cp .publish/Microsoft.NET.StringTools.dll              $DEST/
   cp .publish/System.Buffers.dll                         $DEST/
   cp .publish/System.Collections.Immutable.dll           $DEST/
   cp .publish/System.Memory.dll                          $DEST/
   cp .publish/System.Runtime.CompilerServices.Unsafe.dll $DEST/
   cp .publish/System.Text.Encodings.Web.dll              $DEST/
   cp .publish/System.Text.Json.dll                       $DEST/
   cp .publish/System.Threading.Tasks.Extensions.dll      $DEST/
   ```

3. **Open** `UnityTestProject/` in Unity Hub

4. **Create test objects** — menu **AkerMcp > Setup Test Scene**

5. **Start the plugin** — menu **Window > AkerMcp**, click **Start**

6. **Launch the MCP server**

   ```bash
   dotnet run --project Server
   ```

   The server discovers the running plugin automatically.

### LLM Configuration

**Claude Desktop / Claude Code**

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

**Cursor / VS Code Copilot**

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

→ get_selection {}
← {"selected": true, "path": "/Player/PlayerCamera", "type": "Camera",
    "components": [...], "properties": [...], "childCount": 0}

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

## Writing an Engine Adapter

Subclass `EnginePluginBase` and implement the required interfaces:

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
| `ISceneNode` | Property get/set, method invocation per object | Yes |
| `IEngineCapabilities` | Type resolution, engine metadata | Yes |
| `IMainThreadDispatcher` | Marshal actions to the engine's main thread | Yes |
| `IEditorContext` | Selection, scene management, console logs | No |
| `IAssetManager` | Asset search, load, save, delete | No |
| `ICompilationSupport` | Script recompilation, error retrieval | No |

Register custom type converters via `TypeRegistry.Instance.RegisterCustomSerializer<T>(...)` for engine-specific structs.

---

## Project Structure

```
aker-mcp/
├── AkerMcp.sln
│
├── Shared/                              AkerMcp.Shared (netstandard2.1)
│   ├── Protocol/                        JSON-RPC and MCP message models
│   ├── Abstraction/                     Engine-agnostic interfaces
│   ├── Reflection/                      PropertyPathResolver, inspector, cache
│   ├── Serialization/                   GenericSerializer, TypeRegistry
│   └── Ipc/                             Named pipe channel, binary framing
│
├── Server/                              AkerMcp.Server (net8.0 console app)
│   ├── McpServer.cs                     JSON-RPC dispatcher, MCP lifecycle
│   ├── ToolRegistry.cs                  11 generic tool definitions
│   ├── ResourceRegistry.cs             5 resource definitions
│   ├── EngineConnection.cs             IPC client to engine plugin
│   └── StdioTransport.cs              stdin/stdout transport
│
├── Client/                              AkerMcp.Client (netstandard2.1)
│   ├── EnginePluginBase.cs             Abstract base for adapters
│   ├── IpcRequestHandler.cs            Request routing and execution
│   ├── PluginDiscovery.cs              Lock-file based auto-discovery
│   └── MainThreadDispatcherBase.cs     Thread-safe queue with TCS pattern
│
└── UnityTestProject/                    Unity 6 test project
    └── Assets/AkerMcp/                  Unity adapter (~500 LOC)
        ├── UnitySceneGraph.cs
        ├── UnitySceneNode.cs
        ├── UnityCapabilities.cs
        ├── UnityEditorContext.cs
        ├── UnityCompilationSupport.cs
        ├── UnityMainThreadDispatcher.cs
        ├── UnityMcpPlugin.cs
        ├── UnityTypeRegistration.cs
        └── Editor/
            ├── McpEditorWindow.cs       Start/Stop UI panel
            └── TestSceneSetup.cs        Menu item to populate a test scene
```

---

## How It Works

1. The **engine plugin** starts a named-pipe server and writes a lock file to the system temp directory with the pipe name.
2. The **MCP server** scans for lock files, connects to the pipe, and begins forwarding tool calls.
3. The **LLM** sends JSON-RPC requests over stdio. The server deserializes them, forwards to the engine plugin via MessagePack-encoded IPC, and returns the result as JSON.
4. The **engine plugin** receives IPC requests, dispatches them to the main thread via `IMainThreadDispatcher`, executes reflection-based operations through `ISceneGraph`/`ISceneNode`, and returns serialized results.

Property paths like `transform.position.x` are resolved at runtime by `PropertyPathResolver`, which walks the object graph via cached reflection metadata. Struct value-type propagation is handled automatically — setting a nested field on a `Vector3` correctly propagates the boxed copy back up the chain.

---

## License

MIT
