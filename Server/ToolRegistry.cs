using System;
using System.Collections.Generic;
using System.Linq;
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

            Register("create_sound",
                @"Create a placeholder sound effect from a small procedural 'sound-spec' (jsfxr-style, authored by you).
The server SYNTHESIZES a WAV and imports it as an audio clip — engine-agnostic, like create_sprite. Perfect for the
flap/jump/score/hit/pickup SFX that make a prototype feel real (a silent prototype feels broken).

sound-spec fields (all optional except pick a wave + freq):
  wave: ""square""|""saw""|""sine""|""triangle""|""noise""   freq: start Hz   freq_sweep: Hz added per second (negative = down)
  attack/sustain/decay: envelope seconds   duration: total seconds (scales the envelope)   volume: 0..1
  vibrato_depth: 0..1   vibrato_rate: Hz
Recipes: jump = square, freq 480, freq_sweep +1200, short. coin = square, freq 900 then 1200 (two calls) or freq_sweep up.
hit/explosion = noise, freq 200, decay ~0.25. laser = saw, freq 1200, freq_sweep -3000.

Example (jump): { ""name"":""jump"", ""spec"":{ ""wave"":""square"",""freq"":480,""freq_sweep"":1400,""attack"":0.005,""sustain"":0.02,""decay"":0.12,""volume"":0.5 } }
Optionally place a source: add ""scene_path"", ""position"", ""volume"", ""loop"", ""auto_play"" (for looping music/ambience) at the top level.",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""name"": { ""type"": ""string"" },
                        ""spec"": {
                            ""type"": ""object"",
                            ""properties"": {
                                ""wave"": { ""type"": ""string"", ""enum"": [""square"", ""saw"", ""sine"", ""triangle"", ""noise""] },
                                ""freq"": { ""type"": ""number"" },
                                ""freq_sweep"": { ""type"": ""number"" },
                                ""attack"": { ""type"": ""number"" }, ""sustain"": { ""type"": ""number"" }, ""decay"": { ""type"": ""number"" },
                                ""duration"": { ""type"": ""number"" }, ""volume"": { ""type"": ""number"" },
                                ""vibrato_depth"": { ""type"": ""number"" }, ""vibrato_rate"": { ""type"": ""number"" },
                                ""sample_rate"": { ""type"": ""integer"" }
                            }
                        },
                        ""scene_path"": { ""type"": ""string"" },
                        ""position"": { ""type"": ""object"" },
                        ""volume"": { ""type"": ""number"" },
                        ""loop"": { ""type"": ""boolean"" },
                        ""auto_play"": { ""type"": ""boolean"" }
                    },
                    ""required"": [""name"", ""spec""]
                }"),
                HandleCreateSound);

            Register("add_primitive",
                @"Drop in a VETTED gameplay behaviour instead of hand-writing (and debugging) it — cuts the
write_script -> refresh_scripts -> fix loop. The server writes the known-good source for the connected engine;
you then add the component and set its public fields.

Call with no args to LIST the catalog. Then add_primitive { ""id"": ""..."" } writes it (override 'path' if you like).
Unity primitives assume the legacy Input Manager is available (Active Input Handling = Both or Input Manager).
Catalog ids: platformer_controller_2d, auto_runner_2d, camera_follow_2d, killzone_2d, score_overlay.

After it writes: call refresh_scripts, then attach the component (execute: obj.AddComponent<T>()) and configure the
returned fields via set_property/execute.",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""id"": { ""type"": ""string"", ""description"": ""primitive id (omit to list the catalog)"" },
                        ""path"": { ""type"": ""string"", ""description"": ""optional target file path (defaults per primitive)"" }
                    }
                }"),
                HandleAddPrimitive);

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

            Register("focus_window",
                @"Bring a window to the foreground on the machine running the server, matched by a case-insensitive substring of its title. Restores the window first if it is minimized.
Use this before capturing an editor whose internal screenshot needs the window in the foreground (e.g. Stride Game Studio), or to surface any app. Call list_windows first to discover titles.

Example: { ""title"": ""Game Studio"" }",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""title"": { ""type"": ""string"", ""description"": ""Substring of the target window's title (case-insensitive)"" }
                    },
                    ""required"": [""title""]
                }"),
                HandleFocusWindow);

            // ---- Runtime loop: run the game, observe it, drive it ----

            Register("enter_play",
                @"Start the project running so you can verify it actually PLAYS — not just how it looks statically.
For a game engine this enters Play Mode / runs the scene; for an animation editor it plays the timeline.

The runtime loop: enter_play -> (send_input to drive it) -> capture_sequence / get_property to observe -> exit_play.

Notes:
- Unity: entering Play Mode reloads the domain and briefly drops the connection; this tool waits for the
  auto-reconnect and reports the settled state — no action needed. take_screenshot('game') shows the running game.
- Godot: the game runs in a SEPARATE window — screenshot it with capture_window (by its title), not take_screenshot.
- Returns NOT_SUPPORTED on engines without play control.",
                ParseSchema(@"{ ""type"": ""object"", ""properties"": {} }"),
                HandleEnterPlay);

            Register("exit_play",
                @"Stop play/playback and return to edit mode (Unity may reload the domain — handled automatically).
Always exit_play when you finish verifying, so the editor is left in a clean editable state.",
                ParseSchema(@"{ ""type"": ""object"", ""properties"": {} }"),
                HandleExitPlay);

            Register("set_play_pause",
                @"Pause (true) or resume (false) an in-progress play/playback. Pause before play_step to inspect frame by frame.",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""paused"": { ""type"": ""boolean"", ""description"": ""true = pause, false = resume (default: true)"" }
                    }
                }"),
                HandleSetPlayPause);

            Register("play_step",
                @"Advance play/playback while paused, to inspect a jump arc or collision frame by frame.
SkelForge steps N whole animation frames exactly. On Unity each call reliably advances ONE frame per editor
tick — multi-frame stepping is best-effort, so call play_step repeatedly and confirm progress with get_play_state.",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""frames"": { ""type"": ""integer"", ""description"": ""Frames to advance (default: 1)"" }
                    }
                }"),
                HandlePlayStep);

            Register("get_play_state",
                @"Report whether the project is playing, paused, the current time, and the clip duration (if any).
Read-only. Use it to confirm enter_play took effect and to watch time advance while running.",
                ParseSchema(@"{ ""type"": ""object"", ""properties"": {} }"),
                HandleGetPlayState,
                new ToolAnnotations { ReadOnlyHint = true });

            Register("capture_sequence",
                @"Capture SEVERAL screenshots at an interval and return them as a strip — so you can see MOTION, not just one pose.
Call this WHILE the project is playing (enter_play first, and don't pause): a moving game/animation is what makes the
frames differ. Great for verifying a jump arc, a scroll, a spawn, a collision, or an animation cycle.

- count: number of frames (1-8, default 4). interval_ms: gap between frames (0-3000, default 500).
- view: 'game' (default) or 'scene'.
- window_title: capture THIS OS window each frame instead of the editor viewport — use it for a SEPARATE-WINDOW
  game (Godot/Stride) by passing the game window's title; without it, Godot/Stride strips show the editor, not the game.",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""count"": { ""type"": ""integer"", ""description"": ""Frames to capture (1-8, default 4)"" },
                        ""interval_ms"": { ""type"": ""integer"", ""description"": ""Milliseconds between frames (0-3000, default 500)"" },
                        ""view"": { ""type"": ""string"", ""enum"": [""game"", ""scene""], ""description"": ""Editor view to capture (default 'game')"" },
                        ""window_title"": { ""type"": ""string"", ""description"": ""Capture this OS window (title substring) each frame — for a separate-window game"" }
                    }
                }"),
                HandleCaptureSequence,
                new ToolAnnotations { ReadOnlyHint = true });

            Register("send_input",
                @"Inject synthetic input into the RUNNING game to test interactive mechanics (jump, move, click).
Call enter_play first. Provide an ordered 'events' array; each event is a key, a mouse button, a mouse move, or a
named engine action.

Event shapes:
- { ""type"":""key"", ""key"":""Space"", ""hold_ms"":60 }            press+release after 60ms (omit hold_ms and set 'pressed' for explicit down/up)
- { ""type"":""key"", ""key"":""Right"", ""pressed"":true }          key down (send a matching pressed:false later to release)
- { ""type"":""mouse_button"", ""button"":""left"", ""hold_ms"":50 }
- { ""type"":""mouse_move"", ""x"":960, ""y"":540 }   x/y are pixels, top-left origin. NOTE: the OS-level path maps them to
     the PRIMARY monitor only, so a windowed game on a secondary monitor / away from the origin may be unreachable — prefer
     keyboard input for OS-level (Godot/Stride) targets. Unity's in-process path uses game-view pixels.
Canonical keys: Space, Enter, Escape, Tab, Up/Down/Left/Right, A-Z, 0-9, Shift, Ctrl, Alt.

Delivery: engine-internal injection when available (Unity drives the new Input System directly), otherwise OS-level
to the focused game window. IMPORTANT: on Godot/Stride the game is a SEPARATE window, so pass 'window_title' with
the game window's title — otherwise the OS-level path focuses the EDITOR and input goes there. On Unity the game
runs in the editor's Game View, so no window_title is needed. (The 'action' event type is reserved but not yet
injectable — drive the key/mouse controls the action is bound to instead.)",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""events"": {
                            ""type"": ""array"",
                            ""description"": ""Ordered list of input events"",
                            ""items"": {
                                ""type"": ""object"",
                                ""properties"": {
                                    ""type"": { ""type"": ""string"", ""enum"": [""key"", ""mouse_button"", ""mouse_move"", ""action""] },
                                    ""key"": { ""type"": ""string"" },
                                    ""button"": { ""type"": ""string"", ""enum"": [""left"", ""right"", ""middle""] },
                                    ""action"": { ""type"": ""string"" },
                                    ""pressed"": { ""type"": ""boolean"" },
                                    ""x"": { ""type"": ""number"" },
                                    ""y"": { ""type"": ""number"" },
                                    ""hold_ms"": { ""type"": ""number"" }
                                }
                            }
                        },
                        ""window_title"": { ""type"": ""string"", ""description"": ""OS-level path only: title substring of the window to focus before injecting"" }
                    },
                    ""required"": [""events""]
                }"),
                HandleSendInput);

            Register("sample_state",
                @"Read live RUNTIME values from the running game — the honest way to verify a mechanic, instead of guessing from pixels.
Call it while playing (after enter_play). Provide 'probes' as { name: ""C# expression"" }; each expression is evaluated in the
engine (via the same Roslyn host as 'execute'), so use engine-appropriate API. Prefer SCALAR expressions (numbers/bools/strings).

Examples (Unity): { ""probes"": { ""birdY"": ""Find(\""Bird\"").transform.position.y"", ""score"": ""GameManager.Instance.Score"" } }
Returns each name's value (or a per-probe error).",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""probes"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""string"" },
                            ""description"": ""map of name -> C# expression evaluated in the engine"" }
                    },
                    ""required"": [""probes""]
                }"),
                HandleSampleState,
                new ToolAnnotations { ReadOnlyHint = true });

            Register("assert_state",
                @"Assert RUNTIME conditions against real engine values and get a structured PASS/FAIL — the reliable alternative to
inferring 'did it work?' from a screenshot. Call during play. Each assertion evaluates a C# 'expression' in the engine and
compares it with 'op' to 'value'. Polls until all pass or 'timeout_ms' elapses — ideal for 'becomes true' conditions
(e.g. isGrounded after a jump lands, score increments after passing a pipe).

ops: '=='  '!='  '<'  '<='  '>'  '>='  'approx' (|a-b|<=1e-3)  'truthy'  'falsy'.
Example: { ""assertions"": [ { ""expression"": ""Find(\""Bird\"").GetComponent<Rigidbody2D>().velocity.y"", ""op"": ""<"", ""value"": 0, ""label"": ""bird falls under gravity"" } ], ""timeout_ms"": 1500 }",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""assertions"": {
                            ""type"": ""array"",
                            ""items"": {
                                ""type"": ""object"",
                                ""properties"": {
                                    ""expression"": { ""type"": ""string"" },
                                    ""op"": { ""type"": ""string"", ""enum"": [""=="", ""!="", ""<"", ""<="", "">"", "">="", ""approx"", ""truthy"", ""falsy""] },
                                    ""value"": {},
                                    ""label"": { ""type"": ""string"" }
                                },
                                ""required"": [""expression"", ""op""]
                            }
                        },
                        ""timeout_ms"": { ""type"": ""integer"", ""description"": ""Poll until all pass or this elapses (default 0 = check once)"" },
                        ""poll_ms"": { ""type"": ""integer"", ""description"": ""Poll interval (default 250)"" }
                    },
                    ""required"": [""assertions""]
                }"),
                HandleAssertState,
                new ToolAnnotations { ReadOnlyHint = true });

            Register("playtest",
                @"Run and VERIFY a prototype in ONE call — the closed-loop way to answer 'does the mechanic work?'.
Drives the game and observes it SERVER-SIDE, so input lands at a precise moment relative to capture/assert (no
multi-round-trip lag between separate tool calls, which otherwise misses fast transients like a jump arc).

Flow: enter_play -> run 'steps' in order -> check final 'criteria' -> exit_play. Returns ONE verdict (PASS/FAIL),
a timeline log, the captured frames (a motion strip), and structured assertion/sample evidence.

Each step is ONE of:
- { ""input"": { ""events"": [ { ""type"":""key"",""key"":""Space"",""hold_ms"":60 } ] } }   (drive it; add window_title for Godot/Stride)
- { ""wait_ms"": 300 }                                                        (let the game advance)
- { ""capture"": true }  or  { ""capture"": { ""view"":""game"" } }                 (add a frame to the strip)
- { ""assert"": [ { ""expression"":""..."", ""op"":""<"", ""value"":0, ""label"":""..."" } ], ""timeout_ms"":1000 }   (verify runtime state, polled)
- { ""sample"": { ""y"":""Find(\""Bird\"").transform.position.y"" } }               (record values as evidence)

'criteria' is a final assert array. Expressions are C# evaluated in the engine (like execute / assert_state); use
engine-appropriate API and prefer scalars. Example: verify a jump —
{ ""steps"": [ {""sample"":{""y0"":""Find(\""P\"").transform.position.y""}}, {""input"":{""events"":[{""type"":""key"",""key"":""Space"",""hold_ms"":60}]}},
  {""wait_ms"":250}, {""capture"":true}, {""assert"":[{""expression"":""Find(\""P\"").transform.position.y"",""op"":"">"",""value"":0.5,""label"":""rose after jump""}],""timeout_ms"":800} ] }",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""enter"": { ""type"": ""boolean"", ""description"": ""enter_play first (default true)"" },
                        ""exit"": { ""type"": ""boolean"", ""description"": ""exit_play at the end (default true)"" },
                        ""steps"": { ""type"": ""array"", ""items"": { ""type"": ""object"" }, ""description"": ""ordered timeline (input/wait_ms/capture/assert/sample)"" },
                        ""criteria"": { ""type"": ""array"", ""items"": { ""type"": ""object"" }, ""description"": ""final acceptance assertions"" },
                        ""criteria_timeout_ms"": { ""type"": ""integer"" }
                    }
                }"),
                HandlePlaytest);
        }

        private Task<ToolResult> HandleFocusWindow(JsonElement? args, CancellationToken ct)
        {
            var capture = PlatformScreenCapture.Current;
            if (capture == null)
                return Task.FromResult(ToolResult.Error(PlatformScreenCapture.UnsupportedPlatformMessage));

            string? title = null;
            if (args is JsonElement a && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("title", out var t))
                title = t.GetString();
            if (string.IsNullOrWhiteSpace(title))
                return Task.FromResult(ToolResult.Error("Missing required 'title' (window title substring)."));

            var ok = capture.FocusWindowByTitle(title, out var err);
            return Task.FromResult(ok
                ? ToolResult.Text($"Brought window matching '{title}' to the foreground.")
                : ToolResult.Error(err ?? "Failed to focus the window."));
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

        private async Task<ToolResult> HandleCreateSound(JsonElement? args, CancellationToken ct)
        {
            if (args is not JsonElement a || a.ValueKind != JsonValueKind.Object)
                return ToolResult.Error("Missing arguments.");

            if (!a.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(nameEl.GetString()))
                return ToolResult.Error("Missing required 'name'.");
            var name = nameEl.GetString()!;

            if (!a.TryGetProperty("spec", out var spec) || spec.ValueKind != JsonValueKind.Object)
                return ToolResult.Error("Missing required 'spec' (sound-spec object).");

            byte[] wav;
            try { wav = SoundSynthesizer.RenderToWav(spec); }
            catch (Exception ex) { return ToolResult.Error($"Sound synthesis failed: {ex.Message}"); }

            var metadata = new Dictionary<string, object?> { ["name"] = name };
            if (a.TryGetProperty("volume", out var v) && v.ValueKind == JsonValueKind.Number)
                metadata["volume"] = v.GetDouble();
            if (a.TryGetProperty("loop", out var lp) && (lp.ValueKind == JsonValueKind.True || lp.ValueKind == JsonValueKind.False))
                metadata["loop"] = lp.GetBoolean();
            if (a.TryGetProperty("auto_play", out var ap) && (ap.ValueKind == JsonValueKind.True || ap.ValueKind == JsonValueKind.False))
                metadata["auto_play"] = ap.GetBoolean();
            if (a.TryGetProperty("scene_path", out var sp) && sp.ValueKind == JsonValueKind.String)
                metadata["scene_path"] = sp.GetString();
            if (a.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Object)
                metadata["position"] = JsonSerializer.Deserialize<Dictionary<string, double>>(pos.GetRawText());

            var metadataJson = JsonSerializer.Serialize(metadata);
            return await _engine.ForwardSoundImport(metadataJson, wav, ct, timeoutMs: 60_000);
        }

        private async Task<ToolResult> HandleAddPrimitive(JsonElement? args, CancellationToken ct)
        {
            var a = args ?? default;
            string? id = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("id", out var idEl)
                && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;

            // No id -> list the catalog.
            if (string.IsNullOrWhiteSpace(id))
            {
                var list = PrimitiveCatalog.All.Select(p => new { id = p.Id, summary = p.Summary, fields = p.Fields, engines = p.Variants.Keys });
                return ToolResult.Text("Available gameplay primitives (call add_primitive with an 'id'):\n" +
                    JsonSerializer.Serialize(list, _jsonOptions));
            }

            var prim = PrimitiveCatalog.Find(id!);
            if (prim == null)
                return ToolResult.Error($"Unknown primitive '{id}'. Available: {string.Join(", ", PrimitiveCatalog.All.Select(p => p.Id))}.");

            var info = await _engine.ForwardResourceRead(IpcConstants.Methods.GetProjectInfo, ct);
            var engineKey = PrimitiveCatalog.EngineKey(info) ?? "unity";
            if (!prim.Variants.TryGetValue(engineKey, out var source))
                return ToolResult.Error($"No '{id}' variant for engine '{engineKey}' yet (available for: {string.Join(", ", prim.Variants.Keys)}).");

            var path = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("path", out var pEl)
                && pEl.ValueKind == JsonValueKind.String ? pEl.GetString()! : prim.DefaultFile;

            var writeArgs = JsonSerializer.SerializeToElement(new { path, content = source });
            var res = await _engine.ForwardToolCall(IpcConstants.Methods.WriteScript, writeArgs, ct);
            if (res.IsError) return res;

            var writeText = res.Content.Count > 0 ? res.Content[0].Text : "";
            return ToolResult.Text(
                $"Added primitive '{prim.Id}' -> {path}\n{writeText}\n" +
                $"Configurable public fields: {string.Join(", ", prim.Fields)}\n" +
                "NEXT: call refresh_scripts, then add the component to your object and set fields via execute/set_property.");
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

        // enter_play / exit_play can trigger a domain reload (Unity) that drops the IPC
        // connection mid-call — same as refresh_scripts. Treat a disconnect as the
        // transition succeeding: wait for the plugin to auto-restart, then report the
        // settled play state. On engines without a reload, the first forward already
        // returns the state.
        private Task<ToolResult> HandleEnterPlay(JsonElement? args, CancellationToken ct)
            => HandlePlayTransition(IpcConstants.Methods.EnterPlay, "enter play mode", ct);

        private Task<ToolResult> HandleExitPlay(JsonElement? args, CancellationToken ct)
            => HandlePlayTransition(IpcConstants.Methods.ExitPlay, "exit play mode", ct);

        private async Task<ToolResult> HandlePlayTransition(string method, string label, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await _engine.ForwardToolCall(method, null, ct, timeoutMs: 120_000);

            bool reloaded = false;
            if (IsDisconnectError(result))
            {
                // Unity domain reload dropped the connection mid-call — the transition happened.
                if (!await _engine.WaitForConnection(120_000, ct).ConfigureAwait(false))
                    return ToolResult.Error(
                        $"Engine disconnected while trying to {label} and did not reconnect within 2 minutes. " +
                        "Check the editor (it may show a blocking dialog), then call get_play_state.");
                reloaded = true;
            }
            else if (result.IsError)
            {
                return result; // a genuine engine error (e.g. protocol mismatch), not a reload
            }

            // On exit, clear any OS-level keys/buttons left held (an unbalanced pressed:true)
            // so a forgotten key-down can't stay physically stuck at the OS level.
            if (method == IpcConstants.Methods.ExitPlay)
            {
                try { PlatformInput.Current?.ReleaseAll(); } catch { /* best-effort */ }
            }

            // Settle: the engine's own enter/exit response can be OPTIMISTIC — Unity's
            // EnterPlaymode/ExitPlaymode are deferred, so the first result may predate the
            // transition. Poll get_play_state until it reflects the intended state (or a short
            // window elapses) and report the CONFIRMED state, not the optimistic snapshot.
            bool wantPlaying = method == IpcConstants.Methods.EnterPlay;
            var state = result;
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var polled = await _engine.ForwardToolCall(IpcConstants.Methods.GetPlayState, null, ct);
                if (!polled.IsError)
                {
                    state = polled;
                    var txt = polled.Content.Count > 0 ? polled.Content[0].Text : "";
                    var isPlaying = ReadJsonBoolNullable(txt, "isPlaying");
                    if (isPlaying == wantPlaying) break;                 // reached the intended state
                    if (ReadJsonBool(txt, "supported", true) == false) break; // engine can't report it
                }
                await Task.Delay(200, ct).ConfigureAwait(false);
            }

            var text = state.Content.Count > 0 ? state.Content[0].Text : null;
            var prefix = reloaded ? $"Engine reloaded ({sw.Elapsed.TotalSeconds:0.#}s). " : "";
            return ToolResult.Text($"{prefix}{label} completed.\n{text}");
        }

        private Task<ToolResult> HandleGetPlayState(JsonElement? args, CancellationToken ct)
            => _engine.ForwardToolCall(IpcConstants.Methods.GetPlayState, args, ct);

        private Task<ToolResult> HandleSetPlayPause(JsonElement? args, CancellationToken ct)
            => _engine.ForwardToolCall(IpcConstants.Methods.SetPlayPause, args, ct);

        private Task<ToolResult> HandlePlayStep(JsonElement? args, CancellationToken ct)
            => _engine.ForwardToolCall(IpcConstants.Methods.PlayStep, args, ct);

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
            var (jpeg, error) = await CaptureFrameJpeg(args, ct);
            if (jpeg == null)
                return ToolResult.Error(error ?? "Screenshot failed.");

            var base64 = Convert.ToBase64String(jpeg);
            return new ToolResult
            {
                Content = new List<ContentItem> { ContentItem.FromImage(base64, "image/jpeg") }
            };
        }

        // Captures one frame (engine-internal preferred, OS-level fallback) and returns
        // it normalized to JPEG. Shared by take_screenshot and capture_sequence.
        private async Task<(byte[]? jpeg, string? error)> CaptureFrameJpeg(JsonElement? args, CancellationToken ct)
        {
            // Strategy 1: engine-internal capture (highest quality)
            var engineResult = await _engine.ForwardBinaryToolCall(
                IpcConstants.Methods.TakeScreenshot, args, ct);

            byte[]? rawImage = engineResult.Bytes;

            if (rawImage == null)
            {
                // Fall back to OS-level capture whenever the engine returned no image —
                // not only when it's NOT_SUPPORTED, but also when the internal capture
                // FAILED or TIMED OUT. (Stride's embedded editor game pauses its loop
                // when Game Studio isn't the foreground window, so the back-buffer
                // readback never runs — exactly the situation during MCP use. OS-level
                // PrintWindow captures background/occluded windows, so it still works.)
                var capture = PlatformScreenCapture.Current;
                if (capture == null)
                    return (null,
                        engineResult.ErrorCode == IpcConstants.ErrorCodes.NotSupported
                            ? PlatformScreenCapture.UnsupportedPlatformMessage
                            : $"Screenshot failed: {engineResult.Error ?? "unknown error"}");

                StdioTransport.LogInfo(
                    $"Engine capture returned no image ({engineResult.ErrorCode ?? engineResult.Error ?? "null"}); using OS-level fallback.");

                var windowText = await _engine.ForwardResourceRead(
                    IpcConstants.Methods.GetWindowInfo, ct);
                if (string.IsNullOrEmpty(windowText) || windowText.StartsWith("Error:"))
                    return (null, "Cannot capture: engine window info unavailable.");

                JsonElement windowInfo;
                try
                {
                    windowInfo = JsonSerializer.Deserialize<JsonElement>(windowText);
                }
                catch (Exception ex)
                {
                    return (null, $"Failed to parse window info: {ex.Message}");
                }

                if (!windowInfo.TryGetProperty("pid", out var pidElement))
                    return (null, "Window info missing 'pid'.");
                var pid = pidElement.GetInt32();
                if (pid <= 0)
                    return (null, "Engine reports invalid PID.");

                var titlePrefix = windowInfo.TryGetProperty("windowTitlePrefix", out var p)
                    ? (p.GetString() ?? "")
                    : "";

                rawImage = capture.CaptureMainWindow(pid, titlePrefix, out var captureError);
                if (rawImage == null || rawImage.Length == 0)
                    return (null, captureError ?? "OS-level capture failed.");
            }

            // Normalize: resize + JPEG. Cross-platform via ImageSharp.
            try
            {
                return (ImageProcessor.NormalizeToJpeg(rawImage), null);
            }
            catch (Exception ex)
            {
                return (null, $"Image processing failed: {ex.Message}");
            }
        }

        private async Task<ToolResult> HandleCaptureSequence(JsonElement? args, CancellationToken ct)
        {
            int count = 4, intervalMs = 500;
            JsonElement viewArgs = default;
            bool hasView = false;
            string? windowTitle = null;
            if (args is JsonElement a && a.ValueKind == JsonValueKind.Object)
            {
                // Read via ReadDim (TryGetDouble+round) so a fractional literal like 4.0 for a
                // schema-declared integer doesn't throw and abort the whole capture.
                count = ReadDim(a, "count", count);
                intervalMs = ReadDim(a, "interval_ms", intervalMs);
                windowTitle = ReadString(a, "window_title");
                if (a.TryGetProperty("view", out var v) && v.ValueKind == JsonValueKind.String)
                {
                    // Whitelist the value (don't interpolate arbitrary text into JSON).
                    var view = v.GetString();
                    if (view == "game" || view == "scene")
                    {
                        viewArgs = JsonSerializer.Deserialize<JsonElement>($"{{\"view\":\"{view}\"}}");
                        hasView = true;
                    }
                }
            }

            count = Math.Max(1, Math.Min(8, count));           // cap frames (payload + time)
            intervalMs = Math.Max(0, Math.Min(3000, intervalMs));
            JsonElement? capArgs = hasView ? viewArgs : args;
            bool byWindow = !string.IsNullOrWhiteSpace(windowTitle);

            var content = new List<ContentItem>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
            {
                if (i > 0 && intervalMs > 0)
                    await Task.Delay(intervalMs, ct).ConfigureAwait(false);

                // With window_title, capture that OS window (the separate-window game on
                // Godot/Stride) instead of the engine's editor viewport.
                var (jpeg, error) = byWindow
                    ? CaptureWindowJpeg(windowTitle!)
                    : await CaptureFrameJpeg(capArgs, ct);
                if (jpeg == null)
                {
                    // Bail on first failure, but keep any frames already captured.
                    content.Add(ContentItem.FromText($"frame {i + 1}/{count} failed: {error}"));
                    break;
                }
                content.Add(ContentItem.FromText($"frame {i + 1}/{count} @ ~{sw.Elapsed.TotalSeconds:0.0}s"));
                content.Add(ContentItem.FromImage(Convert.ToBase64String(jpeg), "image/jpeg"));
            }

            if (content.Count == 0)
                return ToolResult.Error("capture_sequence produced no frames.");
            return new ToolResult { Content = content };
        }

        // Capture an OS window (by title substring) as a normalized JPEG — for capture_sequence's
        // window_title mode (a separate-window game). Mirrors the capture_window tool.
        private static (byte[]? jpeg, string? error) CaptureWindowJpeg(string title)
        {
            var capture = PlatformScreenCapture.Current;
            if (capture == null) return (null, PlatformScreenCapture.UnsupportedPlatformMessage);
            var raw = capture.CaptureWindowByTitle(title, out var err);
            if (raw == null) return (null, err ?? "Window capture failed.");
            try { return (ImageProcessor.NormalizeToJpeg(raw), null); }
            catch (Exception ex) { return (null, $"Image processing failed: {ex.Message}"); }
        }

        private async Task<ToolResult> HandleSendInput(JsonElement? args, CancellationToken ct)
        {
            // Strategy 1: engine-internal injection (IInputSimulator). The engine returns
            // an InputResult JSON; supported:false means "fall back to OS-level".
            var engineResult = await _engine.ForwardToolCall(IpcConstants.Methods.SendInput, args, ct);
            if (IsDisconnectError(engineResult))
                return engineResult;
            if (!engineResult.IsError)
            {
                var text = engineResult.Content.Count > 0 ? engineResult.Content[0].Text : "";
                if (ReadJsonBool(text, "supported", defaultValue: true))
                    return engineResult; // engine handled it in-process
            }

            // Strategy 2: OS-level fallback (focus the target window, then SendInput).
            var injector = PlatformInput.Current;
            if (injector == null)
                return ToolResult.Error(PlatformInput.UnsupportedPlatformMessage);

            var events = ParseInputEvents(args);
            if (events.Count == 0)
                return ToolResult.Error("No input events supplied. Provide an 'events' array, e.g. " +
                    "{ \"events\": [ { \"type\": \"key\", \"key\": \"Space\", \"hold_ms\": 60 } ] }.");

            var capture = PlatformScreenCapture.Current;
            string? focusTitle = ReadString(args, "window_title");
            if (string.IsNullOrWhiteSpace(focusTitle))
                focusTitle = await GetEngineWindowTitle(ct);
            string focusNote = "";
            if (capture != null && !string.IsNullOrWhiteSpace(focusTitle))
            {
                if (capture.FocusWindowByTitle(focusTitle!, out var focusErr))
                    focusNote = $" (focused '{focusTitle}')";
                else
                    focusNote = $" (could not focus '{focusTitle}': {focusErr})";
            }

            if (!injector.Inject(events, out var dispatched, out var err))
                return ToolResult.Error($"OS-level input injection failed: {err}");

            var suffix = string.IsNullOrEmpty(err) ? "" : $" {err}";
            return ToolResult.Text(
                $"Injected {dispatched}/{events.Count} input event(s) via OS-level SendInput{focusNote}.{suffix}");
        }

        // Reads the engine's main-window title (from get_window_info) to focus before OS-level input.
        private async Task<string?> GetEngineWindowTitle(CancellationToken ct)
        {
            var windowText = await _engine.ForwardResourceRead(IpcConstants.Methods.GetWindowInfo, ct);
            if (string.IsNullOrEmpty(windowText) || windowText.StartsWith("Error:"))
                return null;
            try
            {
                var info = JsonSerializer.Deserialize<JsonElement>(windowText);
                if (info.TryGetProperty("windowTitle", out var wt) && !string.IsNullOrWhiteSpace(wt.GetString()))
                    return wt.GetString();
                if (info.TryGetProperty("windowTitlePrefix", out var wp))
                    return wp.GetString();
            }
            catch { /* fall through */ }
            return null;
        }

        private static string? ReadString(JsonElement? args, string name)
        {
            if (args is JsonElement a && a.ValueKind == JsonValueKind.Object
                && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            return null;
        }

        private static bool ReadJsonBool(string? json, string prop, bool defaultValue)
            => ReadJsonBoolNullable(json, prop) ?? defaultValue;

        // Returns the bool value of a top-level JSON property, or null if absent / not JSON / not a bool.
        private static bool? ReadJsonBoolNullable(string? json, string prop)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var el = JsonSerializer.Deserialize<JsonElement>(json);
                if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v)
                    && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                    return v.GetBoolean();
            }
            catch { /* not JSON */ }
            return null;
        }

        private static List<Shared.Abstraction.InputEvent> ParseInputEvents(JsonElement? args)
        {
            var list = new List<Shared.Abstraction.InputEvent>();
            if (args is not JsonElement a || a.ValueKind != JsonValueKind.Object) return list;
            if (!a.TryGetProperty("events", out var evs) || evs.ValueKind != JsonValueKind.Array) return list;

            foreach (var e in evs.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                list.Add(new Shared.Abstraction.InputEvent
                {
                    Type = e.TryGetProperty("type", out var t) ? (t.GetString() ?? "key") : "key",
                    Key = e.TryGetProperty("key", out var k) ? k.GetString() : null,
                    Button = e.TryGetProperty("button", out var b) ? b.GetString() : null,
                    Action = e.TryGetProperty("action", out var ac) ? ac.GetString() : null,
                    Pressed = !e.TryGetProperty("pressed", out var pr) || pr.GetBoolean(),
                    X = e.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0,
                    Y = e.TryGetProperty("y", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetDouble() : 0,
                    HoldMs = e.TryGetProperty("hold_ms", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetDouble() : 0,
                });
            }
            return list;
        }

        // ---- Runtime verification: read/assert live engine values via the execute host ----

        private async Task<ToolResult> HandleSampleState(JsonElement? args, CancellationToken ct)
        {
            if (args is not JsonElement a || !a.TryGetProperty("probes", out var probes)
                || probes.ValueKind != JsonValueKind.Object)
                return ToolResult.Error("Missing 'probes' object, e.g. { \"probes\": { \"y\": \"Find(\\\"Bird\\\").transform.position.y\" } }.");

            var samples = new List<object>();
            foreach (var p in probes.EnumerateObject())
            {
                var (val, err) = await SampleOne(p.Value.GetString() ?? "", ct);
                samples.Add(new { name = p.Name, value = val, error = err });
            }
            return ToolResult.Text(JsonSerializer.Serialize(new { samples }, _jsonOptions));
        }

        private async Task<ToolResult> HandleAssertState(JsonElement? args, CancellationToken ct)
        {
            if (args is not JsonElement a || !a.TryGetProperty("assertions", out var asserts)
                || asserts.ValueKind != JsonValueKind.Array)
                return ToolResult.Error("Missing 'assertions' array.");

            var (allPass, report) = await RunAssertions(
                asserts, ReadDim(a, "timeout_ms", 0), ReadDim(a, "poll_ms", 250), ct);
            return ToolResult.Text(JsonSerializer.Serialize(new { passed = allPass, assertions = report }, _jsonOptions));
        }

        // Evaluate an array of {expression, op, value, label} against live engine values,
        // polling until all pass or timeoutMs elapses. Shared by assert_state and playtest.
        private async Task<(bool passed, List<object> report)> RunAssertions(
            JsonElement assertsArray, int timeoutMs, int pollMs, CancellationToken ct)
        {
            var items = new List<JsonElement>();
            foreach (var e in assertsArray.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object) items.Add(e);

            pollMs = Math.Max(50, pollMs);
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMs));
            var report = new List<object>();
            bool allPass;

            while (true)
            {
                report = new List<object>();
                allPass = true;
                foreach (var it in items)
                {
                    var expr = it.TryGetProperty("expression", out var ex) ? (ex.GetString() ?? "") : "";
                    var op = it.TryGetProperty("op", out var o) ? (o.GetString() ?? "==") : "==";
                    JsonElement? expected = it.TryGetProperty("value", out var vv) ? vv : (JsonElement?)null;
                    var label = it.TryGetProperty("label", out var lb) ? lb.GetString() : null;

                    var (val, err) = await SampleOne(expr, ct);
                    bool pass = err == null && EvalAssertion(val, op, expected, out _);
                    if (!pass) allPass = false;

                    report.Add(new
                    {
                        label,
                        expression = expr,
                        op,
                        expected,
                        observed = err ?? (val.HasValue ? val.Value.GetRawText() : "(null)"),
                        pass,
                        error = err,
                    });
                }

                if (allPass || DateTime.UtcNow >= deadline || ct.IsCancellationRequested) break;
                await Task.Delay(pollMs, ct).ConfigureAwait(false);
            }

            return (allPass, report);
        }

        // Evaluate one C# expression in the engine via the execute host and return its JSON value.
        private async Task<(JsonElement? value, string? error)> SampleOne(string expr, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(expr)) return (null, "empty expression");
            var argsEl = JsonSerializer.SerializeToElement(new { code = "return (object)(" + expr + ");", timeout_ms = 3000 });
            var res = await _engine.ForwardToolCall(IpcConstants.Methods.Execute, argsEl, ct);
            if (res.IsError) return (null, res.Content.Count > 0 ? res.Content[0].Text : "execute failed");

            var txt = (res.Content.Count > 0 ? res.Content[0].Text : "") ?? "";
            try
            {
                var el = JsonSerializer.Deserialize<JsonElement>(txt);
                if (el.ValueKind == JsonValueKind.Object)
                {
                    if (el.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.False)
                        return (null, el.TryGetProperty("error", out var e) ? (e.GetString() ?? "eval error") : "eval error");
                    if (el.TryGetProperty("returnValue", out var rv))
                        return (rv.Clone(), null);
                }
                return (null, "no returnValue (does this engine support execute?)");
            }
            catch (Exception ex) { return (null, "parse error: " + ex.Message); }
        }

        private static bool EvalAssertion(JsonElement? observed, string op, JsonElement? expected, out string obsStr)
        {
            obsStr = observed.HasValue ? observed.Value.GetRawText() : "(null)";
            op = (op ?? "==").Trim().ToLowerInvariant();

            if (op == "truthy") return observed.HasValue && IsTruthy(observed.Value);
            if (op == "falsy") return !observed.HasValue || !IsTruthy(observed.Value);
            if (!observed.HasValue) return false;
            var o = observed.Value;

            if (op == "==") return expected.HasValue && JsonEquals(o, expected.Value);
            if (op == "!=") return !expected.HasValue || !JsonEquals(o, expected.Value);

            if (TryNum(o, out var av) && expected.HasValue && TryNum(expected.Value, out var bv))
            {
                switch (op)
                {
                    case "<": return av < bv;
                    case "<=": return av <= bv;
                    case ">": return av > bv;
                    case ">=": return av >= bv;
                    case "approx": return Math.Abs(av - bv) <= 1e-3;
                }
            }
            return false;
        }

        private static bool IsTruthy(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.True: return true;
                case JsonValueKind.False: case JsonValueKind.Null: return false;
                case JsonValueKind.Number: return e.TryGetDouble(out var d) && d != 0;
                case JsonValueKind.String:
                    var s = e.GetString();
                    return !string.IsNullOrEmpty(s)
                        && !s.Equals("false", StringComparison.OrdinalIgnoreCase) && s != "0";
                default: return true; // object/array present
            }
        }

        private static bool TryNum(JsonElement e, out double d)
        {
            if (e.ValueKind == JsonValueKind.Number) return e.TryGetDouble(out d);
            if (e.ValueKind == JsonValueKind.String
                && double.TryParse(e.GetString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out d))
                return true;
            d = 0;
            return false;
        }

        private static bool JsonEquals(JsonElement a, JsonElement b)
        {
            if (TryNum(a, out var an) && TryNum(b, out var bn)) return Math.Abs(an - bn) < 1e-9;
            if ((a.ValueKind == JsonValueKind.True || a.ValueKind == JsonValueKind.False)
                && (b.ValueKind == JsonValueKind.True || b.ValueKind == JsonValueKind.False))
                return a.GetBoolean() == b.GetBoolean();
            return string.Equals(
                a.ValueKind == JsonValueKind.String ? a.GetString() : a.GetRawText(),
                b.ValueKind == JsonValueKind.String ? b.GetString() : b.GetRawText(),
                StringComparison.Ordinal);
        }

        // One-call acceptance harness: enter play, run a timed timeline (input/wait/capture/
        // assert/sample) server-side so input lands at a precise moment relative to observation,
        // check final criteria, exit — returns ONE verdict + a motion strip + structured evidence.
        private async Task<ToolResult> HandlePlaytest(JsonElement? args, CancellationToken ct)
        {
            var a = args ?? default;
            bool hasArgs = a.ValueKind == JsonValueKind.Object;
            bool doEnter = ReadBool(a, "enter", true);
            bool doExit = ReadBool(a, "exit", true);

            var content = new List<ContentItem>();
            var log = new System.Text.StringBuilder();
            var assertionReports = new List<object>();
            var samplesLog = new List<object>();
            bool overallPass = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            if (doEnter)
            {
                var r = await HandlePlayTransition(IpcConstants.Methods.EnterPlay, "enter play mode", ct);
                if (r.IsError)
                    return ToolResult.Error($"playtest: enter_play failed: {(r.Content.Count > 0 ? r.Content[0].Text : "")}");
                log.AppendLine($"[{sw.Elapsed.TotalSeconds:0.0}s] enter_play ok");
            }

            if (hasArgs && a.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
            {
                int idx = 0;
                foreach (var step in steps.EnumerateArray())
                {
                    if (step.ValueKind != JsonValueKind.Object) continue;
                    idx++;
                    ct.ThrowIfCancellationRequested();

                    if (step.TryGetProperty("wait_ms", out var w) && w.ValueKind == JsonValueKind.Number)
                    {
                        var ms = Math.Max(0, Math.Min(10_000, (int)w.GetDouble()));
                        await Task.Delay(ms, ct).ConfigureAwait(false);
                        log.AppendLine($"[{sw.Elapsed.TotalSeconds:0.0}s] wait {ms}ms");
                    }
                    else if (step.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
                    {
                        var r = await HandleSendInput(input, ct);
                        var rt = r.Content.Count > 0 ? r.Content[0].Text : "";
                        log.AppendLine($"[{sw.Elapsed.TotalSeconds:0.0}s] input -> {(r.IsError ? "ERROR: " : "")}{Truncate(rt, 100)}");
                    }
                    else if (step.TryGetProperty("capture", out var cap))
                    {
                        JsonElement? capArgs = cap.ValueKind == JsonValueKind.Object ? cap : (JsonElement?)null;
                        var (jpeg, err) = await CaptureFrameJpeg(capArgs, ct);
                        if (jpeg != null)
                        {
                            content.Add(ContentItem.FromText($"frame @ {sw.Elapsed.TotalSeconds:0.0}s (step {idx})"));
                            content.Add(ContentItem.FromImage(Convert.ToBase64String(jpeg), "image/jpeg"));
                            log.AppendLine($"[{sw.Elapsed.TotalSeconds:0.0}s] capture ok");
                        }
                        else log.AppendLine($"[{sw.Elapsed.TotalSeconds:0.0}s] capture failed: {err}");
                    }
                    else if (step.TryGetProperty("assert", out var asserts) && asserts.ValueKind == JsonValueKind.Array)
                    {
                        var (pass, rep) = await RunAssertions(asserts, ReadDim(step, "timeout_ms", 0), ReadDim(step, "poll_ms", 200), ct);
                        overallPass &= pass;
                        assertionReports.Add(new { step = idx, at = Math.Round(sw.Elapsed.TotalSeconds, 1), passed = pass, assertions = rep });
                        log.AppendLine($"[{sw.Elapsed.TotalSeconds:0.0}s] assert -> {(pass ? "PASS" : "FAIL")}");
                    }
                    else if (step.TryGetProperty("sample", out var sam) && sam.ValueKind == JsonValueKind.Object)
                    {
                        var vals = new List<object>();
                        foreach (var p in sam.EnumerateObject())
                        {
                            var (val, err) = await SampleOne(p.Value.GetString() ?? "", ct);
                            vals.Add(new { name = p.Name, value = val, error = err });
                        }
                        samplesLog.Add(new { step = idx, at = Math.Round(sw.Elapsed.TotalSeconds, 1), values = vals });
                        log.AppendLine($"[{sw.Elapsed.TotalSeconds:0.0}s] sample ({vals.Count})");
                    }
                }
            }

            if (hasArgs && a.TryGetProperty("criteria", out var crit) && crit.ValueKind == JsonValueKind.Array)
            {
                var (pass, rep) = await RunAssertions(crit, ReadDim(a, "criteria_timeout_ms", 0), 200, ct);
                overallPass &= pass;
                assertionReports.Add(new { step = "criteria", passed = pass, assertions = rep });
                log.AppendLine($"[{sw.Elapsed.TotalSeconds:0.0}s] criteria -> {(pass ? "PASS" : "FAIL")}");
            }

            if (doExit)
            {
                var r = await HandlePlayTransition(IpcConstants.Methods.ExitPlay, "exit play mode", ct);
                log.AppendLine($"[{sw.Elapsed.TotalSeconds:0.0}s] exit_play {(r.IsError ? "ERROR" : "ok")}");
            }

            var verdict = new
            {
                verdict = overallPass ? "PASS" : "FAIL",
                elapsedS = Math.Round(sw.Elapsed.TotalSeconds, 1),
                assertions = assertionReports,
                samples = samplesLog,
            };
            var summary = $"playtest {(overallPass ? "PASSED" : "FAILED")} ({sw.Elapsed.TotalSeconds:0.#}s)\n\n" +
                          "TIMELINE:\n" + log + "\nRESULT:\n" +
                          JsonSerializer.Serialize(verdict, _jsonOptions);
            content.Insert(0, ContentItem.FromText(summary));
            return new ToolResult { Content = content };
        }

        private static string Truncate(string? s, int n)
            => string.IsNullOrEmpty(s) ? "" : (s!.Length <= n ? s : s.Substring(0, n) + "…");

        private static bool ReadBool(JsonElement obj, string name, bool dflt)
        {
            if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.True) return true;
                if (v.ValueKind == JsonValueKind.False) return false;
            }
            return dflt;
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
