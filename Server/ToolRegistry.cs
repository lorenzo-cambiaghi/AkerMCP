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
Use 'recursive: true' to also delete all children. Paths are case-sensitive.",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""object_path"": { ""type"": ""string"", ""description"": ""Path to the object to delete"" },
                        ""recursive"": { ""type"": ""boolean"", ""description"": ""Delete children recursively"" }
                    },
                    ""required"": [""object_path""]
                }"),
                (args, ct) => _engine.ForwardToolCall("delete", args, ct),
                new ToolAnnotations { DestructiveHint = true });

            Register("refresh_scripts",
                @"Force recompilation of all scripts in the project.
ALWAYS call this after creating or modifying any .cs file. Then call get_compile_errors to verify.
Never assume a script change compiled successfully — always verify.

Workflow: edit .cs file → refresh_scripts → get_compile_errors → fix if needed → repeat.",
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""wait_for_completion"": { ""type"": ""boolean"", ""description"": ""Wait for compilation to finish before returning (default: true)"" }
                    }
                }"),
                (args, ct) => _engine.ForwardToolCall("refresh_scripts", args, ct));

            Register("get_compile_errors",
                @"Get current script compilation errors and warnings.
Always call this after refresh_scripts. Returns file path, line number, column, and error message.
Use 'errors_only: true' to skip warnings and focus on blockers.",
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

Important rules:
1. Each script execution is independent — variables do not persist between calls.
2. ALWAYS return a meaningful value at the end of your script (e.g. `return ""Spawned 10 items"";`).
3. If you need to modify many objects, use this tool instead of calling `set_property` in a loop.",
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
