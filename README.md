

																									
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

# MCPSharp

Generic [Model Context Protocol](https://modelcontextprotocol.io/) bridge for game engines. Instead of implementing hundreds of engine-specific MCP tools, MCPSharp exposes **11 generic, reflection-based tools** that work across Unity, Godot, and any .NET-compatible engine.

## Why

Existing MCP integrations for game engines require 100+ hand-written tool classes — one for each operation type. Every new component or API change means new code.

MCPSharp takes a different approach: **reflection + property paths + generic serialization**. One `set_property` tool can modify any property on any object in any engine. The engine-specific adapter is ~500 lines.

## Architecture

```
LLM (Claude, GPT, ...)
    │ JSON-RPC 2.0 (MCP)
    ▼
┌──────────────┐
│  MCP Server  │  ← standalone .NET 8 process (Server/)
│  stdio/HTTP  │
└──────┬───────┘
       │ Named Pipe + MessagePack
       ▼
┌──────────────┐
│ Engine Plugin│  ← runs inside Unity/Godot (Client/)
│ IPC handler  │
└──────────────┘
```

| Layer | Project | Target | Role |
|-------|---------|--------|------|
| **Shared** | `MCPSharp.Shared` | netstandard2.1 | Protocol models, abstractions, reflection engine, serialization, IPC |
| **Server** | `MCPSharp.Server` | net8.0 | MCP server process — talks JSON-RPC to LLMs, routes to engine via IPC |
| **Client** | `MCPSharp.Client` | netstandard2.1 | Engine plugin base — runs inside the engine, executes on main thread |

`netstandard2.1` ensures compatibility with Unity (2021+) and Godot (.NET).

## MCP Tools

| Tool | Description | Annotations |
|------|-------------|-------------|
| `inspect` | Inspect properties, methods, and children of any scene object or type | readOnly |
| `get_property` | Read any property via dot-notation path (e.g. `transform.position.x`) | readOnly |
| `set_property` | Write any property, including structs, arrays, nested objects | — |
| `call_method` | Invoke any method on scene objects or static classes | — |
| `query` | Find objects by type, name pattern, tag, or property values | readOnly |
| `create` | Create new objects/nodes with initial properties | — |
| `delete` | Remove objects from the scene (with undo support) | destructive |
| `refresh_scripts` | Force script recompilation and return compilation errors/warnings | — |
| `get_compile_errors` | Get current compilation errors with file, line, column | readOnly |
| `get_console_logs` | Read engine console with level/text filtering | readOnly |
| `execute` | Run arbitrary C# code in the engine context (escape hatch) | destructive |

## MCP Resources

| URI | Description |
|-----|-------------|
| `scene://hierarchy` | Current scene tree |
| `project://info` | Engine name, version, project path, current scene |
| `editor://logs` | Recent console log entries |
| `editor://compile_status` | Compilation status with error/warning counts |
| `engine://types` | List of registered engine types |

## Supported Types

The serializer handles arbitrary structs, arrays, lists, and dictionaries via reflection. Engine adapters can register custom converters for optimal handling. The Unity adapter registers:

`Vector2`, `Vector3`, `Vector4`, `Vector2Int`, `Vector3Int`, `Quaternion`, `Color`, `Color32`, `Rect`, `RectInt`, `Bounds`, `BoundsInt`, `LayerMask`

Example JSON representations:

```json
{"x": 1.0, "y": 2.0, "z": 3.0}           // Vector3
{"r": 1.0, "g": 0.0, "b": 0.0, "a": 1.0} // Color
{"center": {"x":0,"y":0,"z":0}, "size": {"x":1,"y":1,"z":1}} // Bounds
[{"x":0,"y":0,"z":0}, {"x":1,"y":1,"z":1}] // Vector3[]
```

## Quick Start (Unity)

### Prerequisites

- .NET 8 SDK
- Unity 2021.3+ (tested with Unity 6000.2)

### 1. Build

```bash
cd MCPSharp
dotnet build -c Release
```

### 2. Copy DLLs to Unity

```bash
# Publish to get all dependencies
dotnet publish Shared/MCPSharp.Shared.csproj -c Release -o /tmp/mcpsharp-publish

# Copy to Unity project
DEST=UnityTestProject/Assets/Plugins/MCPSharp
mkdir -p $DEST
cp /tmp/mcpsharp-publish/MCPSharp.Shared.dll $DEST/
cp Client/bin/Release/netstandard2.1/MCPSharp.Client.dll $DEST/
cp /tmp/mcpsharp-publish/MessagePack.dll $DEST/
cp /tmp/mcpsharp-publish/MessagePack.Annotations.dll $DEST/
cp /tmp/mcpsharp-publish/Microsoft.Bcl.AsyncInterfaces.dll $DEST/
cp /tmp/mcpsharp-publish/Microsoft.NET.StringTools.dll $DEST/
cp /tmp/mcpsharp-publish/System.Buffers.dll $DEST/
cp /tmp/mcpsharp-publish/System.Collections.Immutable.dll $DEST/
cp /tmp/mcpsharp-publish/System.Memory.dll $DEST/
cp /tmp/mcpsharp-publish/System.Runtime.CompilerServices.Unsafe.dll $DEST/
cp /tmp/mcpsharp-publish/System.Text.Encodings.Web.dll $DEST/
cp /tmp/mcpsharp-publish/System.Text.Json.dll $DEST/
cp /tmp/mcpsharp-publish/System.Threading.Tasks.Extensions.dll $DEST/
```

### 3. Open Unity Project

Open `UnityTestProject/` in Unity Hub.

### 4. Setup Test Scene

Menu: **MCPSharp > Setup Test Scene** — creates Player, Enemies, Ground, Lights, Props.

### 5. Start Plugin

Menu: **Window > MCPSharp** — click **Start MCPSharp Plugin**.

### 6. Run MCP Server

```bash
dotnet run --project Server
```

The server auto-discovers the Unity plugin via a lock file in the system temp directory.

### 7. Connect an LLM

#### Claude Desktop / Claude Code

```json
{
  "mcpServers": {
    "game-engine": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/MCPSharp/Server"]
    }
  }
}
```

#### Cursor / VS Code

```json
{
  "mcp": {
    "servers": {
      "game-engine": {
        "command": "dotnet",
        "args": ["run", "--project", "/path/to/MCPSharp/Server"]
      }
    }
  }
}
```

## Example Session

```
LLM → inspect {"target": "/Player"}
    ← {typeName: "Rigidbody", path: "/Player", properties: [{name: "position", type: "Vector3", ...}], childNames: ["PlayerCamera"]}

LLM → set_property {"object_path": "/Player", "property_path": "position", "value": {"x": 10, "y": 0, "z": 5}}
    ← Property 'position' set successfully on /Player

LLM → query {"type_filter": "Camera"}
    ← [{path: "/Player/PlayerCamera", type: "Camera", name: "PlayerCamera"}]

LLM → refresh_scripts {}
    ← Recompilation requested. Status: idle
       Last compile: 14:32:05
       Result: SUCCESS
       No errors or warnings.

LLM → get_console_logs {"level_filter": "error", "count": 10}
    ← (No matching log entries)
```

## Writing an Engine Adapter

To support a new engine, implement these interfaces from `MCPSharp.Shared`:

```csharp
public class MyEnginePlugin : EnginePluginBase
{
    protected override ISceneGraph CreateSceneGraph() => new MySceneGraph();
    protected override IEngineCapabilities CreateCapabilities() => new MyCapabilities();
    protected override IMainThreadDispatcher CreateDispatcher() => new MyDispatcher();

    // Optional
    protected override IEditorContext? CreateEditorContext() => new MyEditorContext();
    protected override IAssetManager? CreateAssetManager() => new MyAssetManager();
    protected override ICompilationSupport? CreateCompilationSupport() => new MyCompilationSupport();

    protected override void Log(string message) { /* engine logging */ }
    protected override void LogError(string message) { /* engine error logging */ }
}
```

The key interfaces:

| Interface | Purpose | Required |
|-----------|---------|----------|
| `ISceneGraph` | Scene tree traversal, object creation/deletion, querying | Yes |
| `ISceneNode` | Property get/set, method invocation on scene objects | Yes |
| `IEngineCapabilities` | Type resolution, engine info | Yes |
| `IMainThreadDispatcher` | Execute actions on the engine's main thread | Yes |
| `IEditorContext` | Editor state, selection, console logs | No |
| `IAssetManager` | Asset search, load, save | No |
| `ICompilationSupport` | Script recompilation, error reporting | No |

Register custom type serializers in the `TypeRegistry` for engine-specific structs.

## Project Structure

```
MCPSharp/
├── Shared/                          # MCPSharp.Shared (netstandard2.1)
│   ├── Protocol/                    # JSON-RPC + MCP models
│   ├── Abstraction/                 # Engine-agnostic interfaces
│   ├── Reflection/                  # PropertyPathResolver, ReflectionInspector, cache
│   ├── Serialization/               # GenericSerializer, TypeRegistry (MessagePack ↔ JSON)
│   └── Ipc/                         # IPC protocol (named pipes, binary framing)
├── Server/                          # MCPSharp.Server (net8.0 console app)
│   ├── McpServer.cs                 # JSON-RPC dispatcher + MCP lifecycle
│   ├── ToolRegistry.cs              # 11 generic tools
│   ├── ResourceRegistry.cs          # 5 resources
│   ├── EngineConnection.cs          # IPC client to engine plugin
│   └── StdioTransport.cs            # stdin/stdout transport
├── Client/                          # MCPSharp.Client (netstandard2.1)
│   ├── EnginePluginBase.cs          # Abstract base for engine adapters
│   ├── IpcRequestHandler.cs         # Routes IPC requests to engine abstractions
│   ├── PluginDiscovery.cs           # Lock file based server↔plugin discovery
│   └── MainThreadDispatcherBase.cs  # Queue + TaskCompletionSource pattern
└── UnityTestProject/                # Complete Unity test project
    └── Assets/MCPSharp/             # Unity adapter implementation
        ├── UnitySceneGraph.cs
        ├── UnitySceneNode.cs
        ├── UnityCapabilities.cs
        ├── UnityEditorContext.cs
        ├── UnityCompilationSupport.cs
        ├── UnityMainThreadDispatcher.cs
        ├── UnityMcpPlugin.cs
        ├── UnityTypeRegistration.cs # Vector3, Color, Bounds, etc.
        └── Editor/
            ├── McpEditorWindow.cs   # Start/Stop UI
            └── TestSceneSetup.cs    # Menu item to create test objects
```

## License

MIT
