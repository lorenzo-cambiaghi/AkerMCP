using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AkerMcp.Server.Platform;
using AkerMcp.Shared.Ipc;
using AkerMcp.Shared.Protocol;

namespace AkerMcp.Server
{
    public class ToolRegistry
    {
        private readonly Dictionary<string, RegisteredTool> _tools = new Dictionary<string, RegisteredTool>();
        private readonly EngineConnection _engine;
        private readonly JsonSerializerOptions _jsonOptions;

        public ToolRegistry(EngineConnection engine)
        {
            _engine = engine;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            };
            RegisterBuiltinTools();
        }

        public ToolListResult ListTools()
        {
            var tools = new List<ToolDefinition>();
            foreach (var entry in _tools.Values)
                tools.Add(entry.Definition);
            return new ToolListResult { Tools = tools };
        }

        public async Task<ToolResult> CallTool(JsonElement paramsElement, CancellationToken ct)
        {
            var callParams = JsonSerializer.Deserialize<ToolCallParams>(paramsElement.GetRawText(), _jsonOptions);
            if (callParams == null || string.IsNullOrEmpty(callParams.Name))
                return ToolResult.Error("Missing tool name");

            if (!_tools.TryGetValue(callParams.Name, out var tool))
                return ToolResult.Error($"Unknown tool: {callParams.Name}");

            try
            {
                return await tool.Handler(callParams.Arguments ?? default, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ToolResult.Error($"Tool '{callParams.Name}' failed: {ex.Message}");
            }
        }

        private void RegisterBuiltinTools()
        {
            Register("inspect",
                @"Inspect all properties, methods, and children of a scene object or type.
ALWAYS call this BEFORE modifying any object. Never guess property names or component types.
Follow the pattern: Inspect → Modify → Verify.

Usage:
- Pass a scene path (e.g. '/Player') to inspect a specific object.
- Pass a type name (e.g. 'Rigidbody') to inspect a type's API.
- Use 'depth: 2' to also inspect children's properties.
- Use 'include_methods: true' to discover callable methods.
- Use 'filter' (regex) to narrow results on large objects.",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""target"": { ""type"": ""string"", ""description"": ""Scene path (e.g. '/root/Player') or type name"" },
                        ""depth"": { ""type"": ""integer"", ""description"": ""Inspection depth (default: 1)"" },
                        ""include_methods"": { ""type"": ""boolean"", ""description"": ""Include method signatures"" },
                        ""filter"": { ""type"": ""string"", ""description"": ""Regex filter on names"" }
                    },
                    ""required"": [""target""]
                }"),
                (args, ct) => _engine.ForwardToolCall("inspect", args, ct),
                new ToolAnnotations { ReadOnlyHint = true });

            Register("get_property",
                @"Get the value of a property using dot-notation path. Use this to verify changes after set_property.

Property path syntax:
- Transform props need no prefix: 'position', 'rotation', 'localScale', 'eulerAngles'
- Other components require a type prefix: 'Rigidbody.mass', 'Camera.fieldOfView', 'Light.intensity'
- Nested access works: 'MeshRenderer.material.color.r'
- Structs are returned as JSON objects: {""x"": 1.0, ""y"": 2.0, ""z"": 3.0}",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""object_path"": { ""type"": ""string"", ""description"": ""Scene path to the object"" },
                        ""property_path"": { ""type"": ""string"", ""description"": ""Dot-notation property path (e.g. 'transform.position.x')"" }
                    },
                    ""required"": [""object_path"", ""property_path""]
                }"),
                (args, ct) => _engine.ForwardToolCall("get_property", args, ct),
                new ToolAnnotations { ReadOnlyHint = true });

            Register("set_property",
                @"Set the value of a property on a scene object. Supports undo. Use for single property changes.
For bulk modifications (10+ objects), use the 'execute' tool instead.

Property path syntax:
- Transform props: 'position', 'localScale', 'eulerAngles' (no prefix needed)
- Component props: 'Rigidbody.mass', 'Light.intensity', 'Camera.fieldOfView'
- Nested: 'MeshRenderer.material.color'

Value formats:
- Primitives: 5, 3.14, true, ""hello""
- Vector3: {""x"": 1, ""y"": 2, ""z"": 3}
- Color: {""r"": 1.0, ""g"": 0.0, ""b"": 0.0, ""a"": 1.0}
- Quaternion: {""x"": 0, ""y"": 0, ""z"": 0, ""w"": 1}

Always verify after setting: call get_property or inspect to confirm the change.",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""object_path"": { ""type"": ""string"", ""description"": ""Scene path to the object"" },
                        ""property_path"": { ""type"": ""string"", ""description"": ""Dot-notation property path"" },
                        ""value"": { ""description"": ""Value to set (can be number, string, bool, or object)"" }
                    },
                    ""required"": [""object_path"", ""property_path"", ""value""]
                }"),
                (args, ct) => _engine.ForwardToolCall("set_property", args, ct));

            Register("call_method",
                @"Invoke a method on a scene object or static class.
Use for actions like SetActive, AddForce, AddComponent, or any static utility method.

Examples:
- target: '/Player', method: 'SetActive', args: ['false']
- target: 'UnityEngine.Application', method: 'get_dataPath' (static)
- All args are passed as strings and converted automatically.",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""target"": { ""type"": ""string"", ""description"": ""Scene path or fully qualified type name"" },
                        ""method"": { ""type"": ""string"", ""description"": ""Method name"" },
                        ""args"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Method arguments in order (as strings)"" }
                    },
                    ""required"": [""target"", ""method""]
                }"),
                (args, ct) => _engine.ForwardToolCall("call_method", args, ct));

            Register("query",
                @"Find objects in the scene by type, name pattern, property value, or tag.
Use this when you don't know the exact path of an object.

Examples:
- Find all cameras: {""type_filter"": ""Camera""}
- Find by name glob: {""name_pattern"": ""Enemy*""}
- Find by tag: {""tag"": ""Player""}
- Combine filters: {""type_filter"": ""Rigidbody"", ""name_pattern"": ""*Boss*""}
- Limit results: {""max_results"": 10}

Paths are case-sensitive. Returns an array of matching objects with their scene paths.",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""type_filter"": { ""type"": ""string"", ""description"": ""Type name to filter by"" },
                        ""name_pattern"": { ""type"": ""string"", ""description"": ""Glob or regex on object name"" },
                        ""property_filter"": { ""type"": ""object"", ""description"": ""Key-value pairs to match"" },
                        ""tag"": { ""type"": ""string"" },
                        ""max_results"": { ""type"": ""integer"", ""description"": ""Max results (default 50)"" }
                    }
                }"),
                (args, ct) => _engine.ForwardToolCall("query", args, ct),
                new ToolAnnotations { ReadOnlyHint = true });

            Register("create",
                @"Create a new object/node in the scene. Supports undo.
For creating a single object with initial properties, use this tool. For spawning many objects or procedural generation, use 'execute' instead.

Examples:
- Empty object: {""type"": ""GameObject"", ""name"": ""Waypoint""}
- Under a parent: {""type"": ""GameObject"", ""name"": ""Child"", ""parent_path"": ""/Parent""}
- With properties: {""type"": ""GameObject"", ""name"": ""Light"", ""properties"": {""position"": {""x"": 0, ""y"": 5, ""z"": 0}}}",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""type"": { ""type"": ""string"", ""description"": ""Type to create (e.g. 'Camera3D', 'RigidBody2D')"" },
                        ""name"": { ""type"": ""string"", ""description"": ""Object name"" },
                        ""parent_path"": { ""type"": ""string"", ""description"": ""Scene path of parent"" },
                        ""properties"": { ""type"": ""object"", ""description"": ""Initial property values"" }
                    },
                    ""required"": [""type""]
                }"),
                (args, ct) => _engine.ForwardToolCall("create", args, ct));

            Register("delete",
                @"Remove an object/node from the scene. This action supports undo.
By default ('recursive: true') the object AND all its children are deleted.
Pass 'recursive: false' to delete only this object — its children are preserved and re-parented to the deleted object's parent.
Paths are case-sensitive.",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""object_path"": { ""type"": ""string"", ""description"": ""Path to the object to delete"" },
                        ""recursive"": { ""type"": ""boolean"", ""description"": ""Also delete children (default: true). If false, children are re-parented to the deleted object's parent."" }
                    },
                    ""required"": [""object_path""]
                }"),
                (args, ct) => _engine.ForwardToolCall("delete", args, ct),
                new ToolAnnotations { DestructiveHint = true });

            Register("refresh_scripts",
                @"Compile pending script changes and return the result (blocks until done, including Unity's domain reload — typically 5-60s).
ALWAYS call this after creating or modifying any .cs file. Works even when the Unity editor is unfocused.
The result already contains errors and warnings: no separate get_compile_errors call is needed.

Workflow: edit .cs files → refresh_scripts → fix any reported errors → repeat.",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""wait_for_completion"": { ""type"": ""boolean"", ""description"": ""Wait for compilation to finish before returning (default: true)"" }
                    }
                }"),
                HandleRefreshScripts);

            Register("get_compile_errors",
                @"Get the result of the last script compilation: status, errors and warnings (file, line, column).
Note: refresh_scripts already returns this report — only call this to re-check state without recompiling.
All errors are listed; warnings are capped at 10. Use 'errors_only: true' to skip warnings.",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""errors_only"": { ""type"": ""boolean"", ""description"": ""Only return errors, skip warnings (default: false)"" }
                    }
                }"),
                (args, ct) => _engine.ForwardToolCall("get_compile_errors", args, ct),
                new ToolAnnotations { ReadOnlyHint = true });

            Register("get_console_logs",
                @"Get recent engine console log entries (errors, warnings, info messages).
Use this for debugging: check after execute calls, after runtime errors, or when something seems wrong.
Filter by level to focus: 'error', 'warning', 'info', or 'all'.
Use 'search' to find specific messages.",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""count"": { ""type"": ""integer"", ""description"": ""Number of recent entries to return (default: 50)"" },
                        ""level_filter"": { ""type"": ""string"", ""description"": ""Filter by level: 'error', 'warning', 'info', or 'all' (default: 'all')"" },
                        ""search"": { ""type"": ""string"", ""description"": ""Filter messages containing this text"" }
                    }
                }"),
                (args, ct) => _engine.ForwardToolCall("get_console_logs", args, ct),
                new ToolAnnotations { ReadOnlyHint = true });

            Register("select",
                @"Select a GameObject in the editor hierarchy.
Highlights it in the Inspector and Scene view. The selected object becomes available as 'selectedObject' in execute scripts.
Useful to direct the user's attention to a specific object.",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""object_path"": { ""type"": ""string"", ""description"": ""Scene path to select (e.g. '/Player/PlayerCamera')"" }
                    },
                    ""required"": [""object_path""]
                }"),
                (args, ct) => _engine.ForwardToolCall("select_object", args, ct));

            Register("get_selection",
                @"Get the currently selected GameObject in the editor, including its path, components, and property summary.
Use this to understand what the user is looking at before making contextual modifications.",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {}
                }"),
                (args, ct) => _engine.ForwardToolCall("get_selection", args, ct),
                new ToolAnnotations { ReadOnlyHint = true });

            Register("execute",
                @"Execute arbitrary C# code directly in the engine's main thread (powered by Roslyn).
This is your most powerful tool. Use it for procedural generation, bulk modifications, complex logic, or accessing Editor APIs.

Available globals (no initialization needed):
- `GameObject? selectedObject`: Currently selected object.
- `GameObject? Find(string name)`: Shortcut for GameObject.Find.
- `T[] FindAll<T>()`: Find all components of type T in the scene.
- `GameObject Create(string name)`: Create a new empty GameObject.
- `void Log(object message)`: Log to the engine console.

Pre-imported namespaces: System, System.Collections.Generic, System.Linq, UnityEngine, UnityEditor.
Need another namespace? Just put `using ...;` directives at the TOP of your snippet — they are hoisted to file scope automatically (e.g. `using System.IO;`, `using static UnityEngine.Mathf;`).

Important rules:
1. Each script execution is independent — variables do not persist between calls. Write self-contained scripts.
2. ALWAYS return a meaningful value at the end of your script (e.g. `return ""Spawned 10 items"";`).
3. If you need to modify many objects, use this tool instead of calling `set_property` in a loop.
4. Console output (Debug.Log / Log) produced during the run is captured and returned in the 'output' field.
5. The timeout (default 5000ms, override with 'timeout_ms') only stops WAITING: a running script CANNOT be aborted and keeps running on the engine main thread. Avoid unbounded loops; after a timeout, verify scene state before retrying.",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""code"": { ""type"": ""string"", ""description"": ""C# code to execute"" },
                        ""timeout_ms"": { ""type"": ""integer"", ""description"": ""Timeout in ms"" }
                    },
                    ""required"": [""code""]
                }"),
                (args, ct) => _engine.ForwardToolCall("execute", args, ct),
                new ToolAnnotations { DestructiveHint = true });

            Register("take_screenshot",
                @"Capture a screenshot of the engine editor and return the image (JPEG).
Use to visually verify scene changes when property values alone aren't enough.

Views:
- 'game' (default): the Game View — what the player sees, no gizmos.
- 'scene': the Scene View — full editor view with gizmos, useful for inspecting placement.

WHEN TO USE:
- After creating/moving/deleting objects — confirm placement looks right.
- After material/color/texture changes — colors can fail silently (pink fallback shaders).
- After lighting changes — intensity is hard to predict numerically.
- After spawning procedural content — verify distribution, density, scale.
- After UI layout changes — anchoring/scaling bugs are visual-only.
- When the user asks 'how does it look?', 'show me', 'did it work?'.

WHEN NOT TO USE:
- After changing non-visual properties (mass, tag, name, layer) — use get_property instead.
- After every micro-change in a sequence — batch the edits, screenshot once at the end.
- To verify a script compiled — use get_compile_errors instead.

Output: auto-resized to max 1920px (long side) and JPEG-encoded at quality 85.
Typical size 150-400 KB, well under model image limits.",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""view"": {
                            ""type"": ""string"",
                            ""enum"": [""game"", ""scene""],
                            ""description"": ""View to capture (default: 'game')""
                        }
                    }
                }"),
                HandleTakeScreenshot,
                new ToolAnnotations { ReadOnlyHint = true });

            Register("create_sprite",
                @"Create a 2D sprite placeholder from a flat-geometric 'shape-spec' (authored by you as JSON).
The server rasterizes it to a PNG and imports it into the engine as a sprite — this works on ANY engine
(Unity/Godot/Stride) because the engine receives a ready raster, not vector data.

HOUSE STYLE: keep placeholders flat, geometric, and clean (recognizable silhouette > detail). This is the
right look for prototypes — abstract-but-clean beats complex-but-ugly. Great for Flappy Bird, Cut the Rope, etc.

shape-spec format:
{
  ""width"": 64, ""height"": 64,           // logical coordinate space
  ""background"": null,                     // null = transparent (usual for sprites)
  ""shapes"": [                              // drawn in order (painter's)
    { ""type"":""ellipse"", ""cx"":32,""cy"":32,""rx"":20,""ry"":18, ""fill"":""#FFCC00"", ""stroke"":""#222"",""strokeWidth"":2 },
    { ""type"":""rect"", ""x"":4,""y"":4,""w"":56,""h"":56,""rx"":8, ""fill"":<paint> },
    { ""type"":""polygon"", ""points"":[[x,y],...], ""fill"":""#FF8800"" },
    { ""type"":""line"", ""points"":[[x,y],...], ""stroke"":""#000"",""strokeWidth"":3 },
    { ""type"":""path"", ""d"":""M0 0 L10 10 Q.. C.. Z"", ""fill"":""#000"" }
  ]
}
A <paint> is a hex string or a linear gradient:
  { ""gradient"":""linear"", ""x1"":0,""y1"":0,""x2"":0,""y2"":64, ""stops"":[{""offset"":0,""color"":""#fff""},{""offset"":1,""color"":""#888""}] }
Optional per-shape ""opacity"" (0..1).

After creating sprites, call take_screenshot to verify how they look.",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""name"": { ""type"": ""string"", ""description"": ""Asset name (no extension)"" },
                        ""spec"": { ""type"": ""object"", ""description"": ""The shape-spec (see description)"" },
                        ""width_px"": { ""type"": ""integer"", ""description"": ""Output width in pixels (default: spec width)"" },
                        ""height_px"": { ""type"": ""integer"", ""description"": ""Output height in pixels (default: spec height)"" },
                        ""pixels_per_unit"": { ""type"": ""number"", ""description"": ""Engine pixels-per-unit (default: 100)"" },
                        ""pivot"": { ""type"": ""object"", ""description"": ""Pivot 0..1, e.g. {\""x\"":0.5,\""y\"":0.5} (default center)"" },
                        ""filter"": { ""type"": ""string"", ""enum"": [""smooth"", ""point""], ""description"": ""Texture filtering (default: smooth)"" },
                        ""scene_path"": { ""type"": ""string"", ""description"": ""Optional: place a sprite node under this path after import"" },
                        ""position"": { ""type"": ""object"", ""description"": ""Optional placement position, e.g. {\""x\"":0,\""y\"":0,\""z\"":0}"" }
                    },
                    ""required"": [""name"", ""spec""]
                }"),
                HandleCreateSprite);

            Register("new_scene",
                @"Create a fresh scene in the engine. Use this to start a prototype from a clean slate.
'two_d: true' (default) sets up a 2D-friendly scene (e.g. orthographic camera, sky background on Unity).
Pass 'save_path' (engine asset path, e.g. 'Assets/Scenes/Flappy.unity' or 'res://scenes/flappy.tscn') to save it.
Not available on every engine (e.g. Stride) — it reports so if unsupported.",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""two_d"": { ""type"": ""boolean"", ""description"": ""2D setup (default: true)"" },
                        ""save_path"": { ""type"": ""string"", ""description"": ""Optional engine asset path to save the new scene"" }
                    }
                }"),
                (args, ct) => _engine.ForwardToolCall(IpcConstants.Methods.NewScene, args, ct),
                new ToolAnnotations { DestructiveHint = true });

            Register("open_scene",
                @"Open an existing scene by its engine asset path (e.g. 'Assets/Scenes/Main.unity' or 'res://scenes/main.tscn').",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""path"": { ""type"": ""string"", ""description"": ""Engine asset path of the scene to open"" }
                    },
                    ""required"": [""path""]
                }"),
                (args, ct) => _engine.ForwardToolCall(IpcConstants.Methods.OpenScene, args, ct),
                new ToolAnnotations { DestructiveHint = true });

            Register("save_scene",
                @"Save the active/edited scene. Omit 'path' to save in place; pass 'path' to save to a new location.",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""path"": { ""type"": ""string"", ""description"": ""Optional engine asset path to save to (default: current path)"" }
                    }
                }"),
                (args, ct) => _engine.ForwardToolCall(IpcConstants.Methods.SaveScene, args, ct));

            Register("write_script",
                @"Write a source file into the engine project (path relative to the project root, e.g.
'Assets/Scripts/Bird.cs' on Unity, 'scripts/bird.gd' on Godot). Creates intermediate folders.
The file lands inside the project regardless of where this MCP server runs (it resolves the project root engine-side).
After writing C# scripts, call refresh_scripts to compile them.",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""path"": { ""type"": ""string"", ""description"": ""Path relative to the project root (or absolute)"" },
                        ""content"": { ""type"": ""string"", ""description"": ""Full file contents"" }
                    },
                    ""required"": [""path"", ""content""]
                }"),
                (args, ct) => _engine.ForwardToolCall(IpcConstants.Methods.WriteScript, args, ct));

            Register("list_platforms",
                @"List the build platforms the engine knows about (e.g. Android, iOS, Windows).
Each entry is flagged whether it is the active build target and whether it can be built on this machine.
Call this first to discover valid platform names for the other platform/build tools.",
                ParseSchema(@"{ ""type"": ""object"", ""properties"": {} }"),
                (args, ct) => _engine.ForwardToolCall(IpcConstants.Methods.ListPlatforms, args, ct),
                new ToolAnnotations { ReadOnlyHint = true });

            Register("get_platform_settings",
                @"Read a platform's build/player settings as a flat key-value map.
ALWAYS call this before set_platform_settings to discover the available keys and current values — keys differ per engine.

Example: { ""platform"": ""Android"" }",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""platform"": { ""type"": ""string"", ""description"": ""Platform name (see list_platforms)"" }
                    },
                    ""required"": [""platform""]
                }"),
                (args, ct) => _engine.ForwardToolCall(IpcConstants.Methods.GetPlatformSettings, args, ct),
                new ToolAnnotations { ReadOnlyHint = true });

            Register("set_platform_settings",
                @"Set one or more of a platform's build/player settings. Pass only the keys you want to change.
Unknown keys are reported back in 'unknownKeys' (not fatal). Verify with get_platform_settings afterwards.

Example: { ""platform"": ""Android"", ""settings"": { ""applicationIdentifier"": ""com.acme.game"", ""minSdkVersion"": ""24"" } }",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""platform"": { ""type"": ""string"", ""description"": ""Platform name (see list_platforms)"" },
                        ""settings"": { ""type"": ""object"", ""description"": ""Key-value settings to apply (keys from get_platform_settings)"" }
                    },
                    ""required"": [""platform"", ""settings""]
                }"),
                (args, ct) => _engine.ForwardToolCall(IpcConstants.Methods.SetPlatformSettings, args, ct));

            Register("switch_build_target",
                @"Make a platform the active build target.
This can trigger a script recompile + domain reload and BLOCKS until done (like refresh_scripts) — typically a few seconds to a minute.
Switch the target before building for a different platform.

Example: { ""platform"": ""Android"" }",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""platform"": { ""type"": ""string"", ""description"": ""Platform to activate (see list_platforms)"" }
                    },
                    ""required"": [""platform""]
                }"),
                HandleSwitchBuildTarget,
                new ToolAnnotations { DestructiveHint = true });

            Register("build_player",
                @"Build the project for a platform (produces an APK/AAB/exe/app bundle, etc.). LONG-RUNNING — can take minutes.
Returns a build report: result, error count, warning count, output path and artifact size.
Make sure the platform is the active build target first (switch_build_target) and that its settings are correct (set_platform_settings).

Example: { ""platform"": ""Android"", ""output_path"": ""Build/game.apk"", ""development"": false }",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""platform"": { ""type"": ""string"", ""description"": ""Platform to build for (see list_platforms)"" },
                        ""output_path"": { ""type"": ""string"", ""description"": ""Output file or directory for the build artifact"" },
                        ""development"": { ""type"": ""boolean"", ""description"": ""Development/debug build (default: false)"" },
                        ""scenes"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Optional explicit scene/level list; engine default if omitted"" }
                    },
                    ""required"": [""platform"", ""output_path""]
                }"),
                HandleBuildPlayer,
                new ToolAnnotations { DestructiveHint = true });

            Register("list_windows",
                @"List the visible top-level windows on the machine running the server (title, process name, pid).
OS-level — works regardless of whether an engine is connected. Use it to find a window title to pass to capture_window.",
                ParseSchema(@"{ ""type"": ""object"", ""properties"": {} }"),
                HandleListWindows,
                new ToolAnnotations { ReadOnlyHint = true });

            Register("capture_window",
                @"Capture a screenshot of ANY visible window on the machine running the server, matched by a case-insensitive substring of its title. Returns a JPEG.
Useful for capturing external apps/tools (browsers, editors, dashboards), not just the game engine. Call list_windows first to discover titles.
The first visible window whose title contains the substring is captured (occluded windows are captured too, without stealing focus).

Example: { ""title"": ""Game Studio"" }",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""title"": { ""type"": ""string"", ""description"": ""Substring of the target window's title (case-insensitive)"" }
                    },
                    ""required"": [""title""]
                }"),
                HandleCaptureWindow,
                new ToolAnnotations { ReadOnlyHint = true });
        }

        private Task<ToolResult> HandleListWindows(JsonElement? args, CancellationToken ct)
        {
            var capture = PlatformScreenCapture.Current;
            if (capture == null)
                return Task.FromResult(ToolResult.Error(PlatformScreenCapture.UnsupportedPlatformMessage));
            var json = JsonSerializer.Serialize(new { windows = capture.ListWindows() }, _jsonOptions);
            return Task.FromResult(ToolResult.Text(json));
        }

        private Task<ToolResult> HandleCaptureWindow(JsonElement? args, CancellationToken ct)
        {
            var capture = PlatformScreenCapture.Current;
            if (capture == null)
                return Task.FromResult(ToolResult.Error(PlatformScreenCapture.UnsupportedPlatformMessage));

            string? title = null;
            if (args is JsonElement a && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("title", out var t))
                title = t.GetString();
            if (string.IsNullOrWhiteSpace(title))
                return Task.FromResult(ToolResult.Error("Missing required 'title' (window title substring)."));

            var raw = capture.CaptureWindowByTitle(title, out var err);
            if (raw == null)
                return Task.FromResult(ToolResult.Error(err ?? "Window capture failed."));

            byte[] outBytes;
            try { outBytes = ImageProcessor.NormalizeToJpeg(raw); }
            catch (Exception ex) { return Task.FromResult(ToolResult.Error($"Image processing failed: {ex.Message}")); }

            var base64 = Convert.ToBase64String(outBytes);
            return Task.FromResult(new ToolResult
            {
                Content = new List<ContentItem> { ContentItem.FromImage(base64, "image/jpeg") }
            });
        }

        private async Task<ToolResult> HandleCreateSprite(JsonElement? args, CancellationToken ct)
        {
            if (args is not JsonElement a || a.ValueKind != JsonValueKind.Object)
                return ToolResult.Error("Missing arguments.");

            if (!a.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(nameEl.GetString()))
                return ToolResult.Error("Missing required 'name'.");
            var name = nameEl.GetString()!;

            if (!a.TryGetProperty("spec", out var spec) || spec.ValueKind != JsonValueKind.Object)
                return ToolResult.Error("Missing required 'spec' (shape-spec object).");

            // Target px defaults to the spec's logical size (1:1), else 128.
            // Read as double then round, so specs that use floats (e.g. "width": 64.0) don't throw.
            int specW = ReadDim(spec, "width", 128);
            int specH = ReadDim(spec, "height", 128);
            int width = ReadDim(a, "width_px", specW);
            int height = ReadDim(a, "height_px", specH);

            byte[] png;
            try
            {
                png = SpriteRasterizer.RenderToPng(spec, width, height);
            }
            catch (Exception ex)
            {
                return ToolResult.Error($"Rasterization failed: {ex.Message}");
            }

            // Build metadata with the snake_case keys the engine handler reads.
            var metadata = new Dictionary<string, object?> { ["name"] = name };
            if (a.TryGetProperty("pixels_per_unit", out var ppu) && ppu.ValueKind == JsonValueKind.Number)
                metadata["pixels_per_unit"] = ppu.GetDouble();
            if (a.TryGetProperty("filter", out var filt) && filt.ValueKind == JsonValueKind.String)
                metadata["filter"] = filt.GetString();
            if (a.TryGetProperty("pivot", out var pivot) && pivot.ValueKind == JsonValueKind.Object)
                metadata["pivot"] = JsonSerializer.Deserialize<Dictionary<string, double>>(pivot.GetRawText());
            if (a.TryGetProperty("scene_path", out var sp) && sp.ValueKind == JsonValueKind.String)
                metadata["scene_path"] = sp.GetString();
            if (a.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Object)
                metadata["position"] = JsonSerializer.Deserialize<Dictionary<string, double>>(pos.GetRawText());

            var metadataJson = JsonSerializer.Serialize(metadata);

            var result = await _engine.ForwardSpriteImport(metadataJson, png, ct, timeoutMs: 60_000);
            return result;
        }

        private async Task<ToolResult> HandleSwitchBuildTarget(JsonElement? args, CancellationToken ct)
        {
            // Switching the active target can recompile scripts and reload the domain,
            // which drops the IPC connection mid-call. Mirror refresh_scripts: treat the
            // disconnect as success, wait for the plugin to come back, then report state.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await _engine.ForwardToolCall(
                IpcConstants.Methods.SwitchBuildTarget, args, ct, timeoutMs: 300_000);

            if (!IsDisconnectError(result))
                return result;

            if (!await _engine.WaitForConnection(180_000, ct).ConfigureAwait(false))
                return ToolResult.Error(
                    "Engine disconnected while switching build target and did not reconnect within 3 minutes. " +
                    "Check the editor (it may show a blocking dialog), then call list_platforms.");

            var report = await _engine.ForwardToolCall(IpcConstants.Methods.ListPlatforms, null, ct);
            var text = report.Content.Count > 0 ? report.Content[0].Text : null;
            return ToolResult.Text(
                $"Build target switch completed (engine reloaded in {sw.Elapsed.TotalSeconds:0.#}s).\n{text}");
        }

        private Task<ToolResult> HandleBuildPlayer(JsonElement? args, CancellationToken ct)
        {
            // Builds are long but do not reload the domain, so a plain forward with a
            // generous timeout is enough (30 min covers most IL2CPP/Gradle builds).
            return _engine.ForwardToolCall(
                IpcConstants.Methods.BuildPlayer, args, ct, timeoutMs: 1_800_000);
        }

        private async Task<ToolResult> HandleRefreshScripts(JsonElement? args, CancellationToken ct)
        {
            // The engine answers directly when compilation fails or there is nothing
            // to compile (no domain reload in either case). On SUCCESS Unity reloads
            // the script domain, which kills the IPC connection mid-call — so a
            // disconnect here is the success signal: wait for the plugin to auto-
            // restart, then fetch the (SessionState-persisted) compile report.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await _engine.ForwardToolCall("refresh_scripts", args, ct, timeoutMs: 180_000);

            if (!IsDisconnectError(result))
                return result;

            if (!await _engine.WaitForConnection(120_000, ct).ConfigureAwait(false))
                return ToolResult.Error(
                    "Engine disconnected during recompilation and did not reconnect within 2 minutes. " +
                    "Check the Unity editor (it may show a blocking dialog), then call get_compile_errors.");

            var report = await _engine.ForwardToolCall("get_compile_errors", null, ct);
            if (report.IsError)
                return report;

            var text = report.Content.Count > 0 ? report.Content[0].Text : null;
            return ToolResult.Text(
                $"Unity recompiled and reloaded the script domain in {sw.Elapsed.TotalSeconds:0.#}s.\n{text}");
        }

        private static bool IsDisconnectError(ToolResult result)
        {
            return result.IsError
                && result.Content.Count > 0
                && result.Content[0].Text?.StartsWith(EngineConnection.EngineDisconnectedPrefix) == true;
        }

        private async Task<ToolResult> HandleTakeScreenshot(JsonElement? args, CancellationToken ct)
        {
            // Strategy 1: engine-internal capture (highest quality)
            var engineResult = await _engine.ForwardBinaryToolCall(
                IpcConstants.Methods.TakeScreenshot, args, ct);

            byte[]? rawImage = engineResult.Bytes;
            string sourceContentType = engineResult.ContentType ?? "image/png";

            if (rawImage == null)
            {
                // If real failure (not NOT_SUPPORTED), propagate
                if (engineResult.ErrorCode != IpcConstants.ErrorCodes.NotSupported)
                    return ToolResult.Error($"Screenshot failed: {engineResult.Error ?? "unknown error"}");

                // Strategy 2: OS-level fallback
                var capture = PlatformScreenCapture.Current;
                if (capture == null)
                    return ToolResult.Error(PlatformScreenCapture.UnsupportedPlatformMessage);

                StdioTransport.LogInfo("Engine has no IScreenCapture; using OS-level fallback.");

                var windowText = await _engine.ForwardResourceRead(
                    IpcConstants.Methods.GetWindowInfo, ct);
                if (string.IsNullOrEmpty(windowText) || windowText.StartsWith("Error:"))
                    return ToolResult.Error("Cannot capture: engine window info unavailable.");

                JsonElement windowInfo;
                try
                {
                    windowInfo = JsonSerializer.Deserialize<JsonElement>(windowText);
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"Failed to parse window info: {ex.Message}");
                }

                if (!windowInfo.TryGetProperty("pid", out var pidElement))
                    return ToolResult.Error("Window info missing 'pid'.");
                var pid = pidElement.GetInt32();
                if (pid <= 0)
                    return ToolResult.Error("Engine reports invalid PID.");

                var titlePrefix = windowInfo.TryGetProperty("windowTitlePrefix", out var p)
                    ? (p.GetString() ?? "")
                    : "";

                rawImage = capture.CaptureMainWindow(pid, titlePrefix, out var captureError);
                if (rawImage == null || rawImage.Length == 0)
                    return ToolResult.Error(captureError ?? "OS-level capture failed.");

                sourceContentType = "image/png";
            }

            // Normalize: resize + JPEG. Cross-platform via ImageSharp.
            byte[] outBytes;
            string outMime;
            try
            {
                outBytes = ImageProcessor.NormalizeToJpeg(rawImage);
                outMime = "image/jpeg";
            }
            catch (Exception ex)
            {
                return ToolResult.Error($"Image processing failed: {ex.Message}");
            }

            var base64 = Convert.ToBase64String(outBytes);
            return new ToolResult
            {
                Content = new List<ContentItem> { ContentItem.FromImage(base64, outMime) }
            };
        }

        private void Register(string name, string description, JsonElement inputSchema,
            Func<JsonElement?, CancellationToken, Task<ToolResult>> handler,
            ToolAnnotations? annotations = null)
        {
            _tools[name] = new RegisteredTool
            {
                Definition = new ToolDefinition
                {
                    Name = name,
                    Description = description,
                    InputSchema = inputSchema,
                    Annotations = annotations
                },
                Handler = handler
            };
        }

        // Reads a numeric dimension as a rounded int, tolerating float JSON values.
        private static int ReadDim(JsonElement obj, string name, int fallback)
        {
            if (obj.ValueKind == JsonValueKind.Object
                && obj.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.Number
                && v.TryGetDouble(out var d))
                return (int)System.Math.Round(d);
            return fallback;
        }

        private static JsonElement ParseSchema(string json)
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }

        private class RegisteredTool
        {
            public ToolDefinition Definition { get; set; } = null!;
            public Func<JsonElement?, CancellationToken, Task<ToolResult>> Handler { get; set; } = null!;
        }
    }
}
