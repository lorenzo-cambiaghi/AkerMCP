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

        /// <summary>The tool profile applied by <see cref="ApplyProfile"/>, or "full" before it runs.</summary>
        public string Profile { get; private set; } = "full";

        /// <summary>Tools the profile hid, in registration order; named in the handshake so the model can ask for them.</summary>
        public IReadOnlyList<string> HiddenTools { get; private set; } = new List<string>();

        /// <summary>Registered tool names, in registration order.</summary>
        public IEnumerable<string> ToolNames => _tools.Keys;
        private readonly JsonSerializerOptions _jsonOptions;

        // Set from a separate-window game's PlayState.WindowTitle on enter_play (cleared on exit),
        // so send_input/capture_sequence auto-route to that window without a manual window_title.
        private volatile string? _gameWindowTitle;

        // The last OS window capture, and what its pixels mean on screen. ui_action converts
        // the coordinates a model read off that image; without it the model would have to
        // undo the 1920-px normalization and add the window origin itself, from numbers no
        // tool ever told it. Written and read from tool-handler threads, so it is swapped
        // whole rather than mutated in place.
        private volatile CaptureAnchor? _lastCapture;

        // Asks the engine whether the agent may act, for the calls that go around the engine
        // rather than through it. Costs one IPC round trip per session against an engine with
        // no policy, and nothing after that.
        private readonly ConsentGate _consent;

        public ToolRegistry(EngineConnection engine)
        {
            _engine = engine;
            _consent = new ConsentGate(engine);
            // A new plugin is a new policy, and a new window: neither the previous engine's
            // consent nor its screenshot nor its game window describes this one.
            engine.OnConnected += () => { _consent.Reset(); _lastCapture = null; _gameWindowTitle = null; };
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            };
            RegisterBuiltinTools();
        }

        /// <summary>
        /// Keep only the tools a profile wants (see <see cref="ToolProfiles"/>). Registration
        /// stays complete and capability-driven; the profile is applied afterwards, so the
        /// loopback test keeps seeing the full surface. Returns (kept, dropped).
        /// </summary>
        public (List<string> kept, List<string> dropped) ApplyProfile(
            string profile, IEnumerable<string>? include = null, IEnumerable<string>? exclude = null)
        {
            var (kept, dropped) = ToolProfiles.Select(_tools.Keys.ToList(), profile, include, exclude);
            foreach (var name in dropped) _tools.Remove(name);
            Profile = profile;
            HiddenTools = dropped;
            return (kept, dropped);
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
            {
                if (HiddenTools.Contains(callParams.Name))
                    return ToolResult.Error(
                        $"Tool '{callParams.Name}' is not loaded in tool profile '{Profile}'. Start the server " +
                        $"with --profile full, AKER_MCP_PROFILE=full, or AKER_MCP_TOOLS_INCLUDE={callParams.Name}.");
                return ToolResult.Error($"Unknown tool: {callParams.Name}");
            }

            try
            {
                // As it came: `?? default` turned a call without `arguments` into an Undefined element that
                // claims a value, and every forwarded tool then failed on GetRawText.
                return await tool.Handler(callParams.Arguments, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // the client cancelled: there is no one to give an error result to
            }
            catch (Exception ex)
            {
                return ToolResult.Error($"Tool '{callParams.Name}' failed: {ex.Message}");
            }
        }

        private void RegisterBuiltinTools()
        {
            Register("inspect", ToolDocs.Describe("inspect"),
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

            Register("get_property", ToolDocs.Describe("get_property"),
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

            Register("set_property", ToolDocs.Describe("set_property"),
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

            Register("call_method", ToolDocs.Describe("call_method"),
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

            Register("query", ToolDocs.Describe("query"),
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""type_filter"": { ""type"": ""string"", ""description"": ""Type name to filter by"" },
                        ""name_pattern"": { ""type"": ""string"", ""description"": ""Glob or regex on object name"" },
                        ""property_filter"": { ""type"": ""object"", ""description"": ""Property path to value, as get_property reads it; an object matches on the members given"" },
                        ""tag"": { ""type"": ""string"" },
                        ""max_results"": { ""type"": ""integer"", ""description"": ""Max results (default 50)"" }
                    }
                }"),
                (args, ct) => _engine.ForwardToolCall("query", args, ct),
                new ToolAnnotations { ReadOnlyHint = true });

            Register("create", ToolDocs.Describe("create"),
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

            Register("delete", ToolDocs.Describe("delete"),
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

            Register("refresh_scripts", ToolDocs.Describe("refresh_scripts"),
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""wait_for_completion"": { ""type"": ""boolean"", ""description"": ""Wait for compilation to finish before returning (default: true)"" }
                    }
                }"),
                HandleRefreshScripts);

            Register("get_compile_errors", ToolDocs.Describe("get_compile_errors"),
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""errors_only"": { ""type"": ""boolean"", ""description"": ""Only return errors, skip warnings (default: false)"" }
                    }
                }"),
                (args, ct) => _engine.ForwardToolCall("get_compile_errors", args, ct),
                new ToolAnnotations { ReadOnlyHint = true });

            Register("get_console_logs", ToolDocs.Describe("get_console_logs"),
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""count"": { ""type"": ""integer"", ""description"": ""Number of recent entries to return (default: 50)"" },
                        ""level_filter"": { ""type"": ""string"", ""enum"": [""error"", ""warning"", ""info"", ""debug"", ""all""] },
                        ""search"": { ""type"": ""string"", ""description"": ""Filter messages containing this text"" }
                    }
                }"),
                (args, ct) => _engine.ForwardToolCall("get_console_logs", args, ct),
                new ToolAnnotations { ReadOnlyHint = true });

            Register("select", ToolDocs.Describe("select"),
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""object_path"": { ""type"": ""string"", ""description"": ""Scene path to select (e.g. '/Player/PlayerCamera')"" }
                    },
                    ""required"": [""object_path""]
                }"),
                (args, ct) => _engine.ForwardToolCall("select_object", args, ct));

            Register("get_selection", ToolDocs.Describe("get_selection"),
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {}
                }"),
                (args, ct) => _engine.ForwardToolCall("get_selection", args, ct),
                new ToolAnnotations { ReadOnlyHint = true });

            Register("execute", ToolDocs.Describe("execute"),
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""code"": { ""type"": ""string"", ""description"": ""C# code to execute"" },
                        ""timeout_ms"": { ""type"": ""integer"", ""description"": ""Timeout in ms"" }
                    },
                    ""required"": [""code""]
                }"),
                // The deadline follows the script's own timeout_ms: with the fixed default a longer script was cut at 30 s.
                (args, ct) => _engine.ForwardToolCall("execute", args, ct, _engine.RequestTimeoutFor(args)),
                new ToolAnnotations { DestructiveHint = true });

            Register("take_screenshot", ToolDocs.Describe("take_screenshot"),
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

            Register("create_sprite", ToolDocs.Describe("create_sprite"),
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

            Register("create_sound", ToolDocs.Describe("create_sound"),
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""name"": { ""type"": ""string"" },
                        ""spec"": {
                            ""type"": ""object"",
                            ""properties"": {
                                ""wave"": { ""type"": ""string"", ""enum"": [""square"", ""saw"", ""sine"", ""triangle"", ""noise""] },
                                ""freq"": { ""type"": ""number"", ""description"": ""Hz; on noise it sets the grain, low = rumble, high = hiss"" },
                                ""freq_sweep"": { ""type"": ""number"" },
                                ""attack"": { ""type"": ""number"" }, ""sustain"": { ""type"": ""number"" }, ""decay"": { ""type"": ""number"" },
                                ""decay_curve"": { ""type"": ""string"", ""enum"": [""exponential"", ""linear""] },
                                ""duration"": { ""type"": ""number"" }, ""volume"": { ""type"": ""number"" },
                                ""punch"": { ""type"": ""number"", ""description"": ""0-1 extra amplitude at the start"" },
                                ""lowpass"": { ""type"": ""number"", ""description"": ""Hz cutoff, 0 = open; the main cure for harshness"" },
                                ""lowpass_sweep"": { ""type"": ""number"", ""description"": ""octaves per second"" },
                                ""resonance"": { ""type"": ""number"", ""description"": ""0-1, emphasis at the cutoff"" },
                                ""highpass"": { ""type"": ""number"" },
                                ""duty"": { ""type"": ""number"", ""description"": ""square only, 0.01-0.99"" },
                                ""duty_sweep"": { ""type"": ""number"" },
                                ""arpeggio"": { ""type"": ""number"", ""description"": ""pitch multiplier applied once, e.g. 1.5"" },
                                ""arpeggio_delay"": { ""type"": ""number"" },
                                ""repeat"": { ""type"": ""number"", ""description"": ""retriggers per second"" },
                                ""vibrato_depth"": { ""type"": ""number"" }, ""vibrato_rate"": { ""type"": ""number"" },
                                ""phaser_offset"": { ""type"": ""number"" }, ""phaser_sweep"": { ""type"": ""number"" },
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

            Register("create_music", ToolDocs.Describe("create_music"),
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""name"": { ""type"": ""string"" },
                        ""spec"": {
                            ""type"": ""object"",
                            ""properties"": {
                                ""engine"": {
                                    ""type"": ""string"",
                                    ""enum"": [""chiptune"", ""soundfont""],
                                    ""description"": ""chiptune (default, always available) or soundfont for sampled General MIDI instruments""
                                },
                                ""bpm"": { ""type"": ""number"" },
                                ""steps_per_beat"": { ""type"": ""integer"", ""description"": ""default 4, so one step is a sixteenth"" },
                                ""repeat"": { ""type"": ""integer"", ""description"": ""play the whole score N times (1-8)"" },
                                ""volume"": { ""type"": ""number"" },
                                ""channels"": {
                                    ""type"": ""array"",
                                    ""items"": {
                                        ""type"": ""object"",
                                        ""properties"": {
                                            ""instrument"": { ""type"": ""string"", ""description"": ""chiptune: lead, bass, pad, arp, pluck, drums. soundfont: also piano, strings, trumpet, flute, guitar, choir, organ, harp and any GM program number"" },
                                            ""notes"": { ""type"": ""string"" },
                                            ""volume"": { ""type"": ""number"" }
                                        },
                                        ""required"": [""instrument"", ""notes""]
                                    }
                                },
                                ""sample_rate"": { ""type"": ""integer"" }
                            },
                            ""required"": [""channels""]
                        },
                        ""scene_path"": { ""type"": ""string"" },
                        ""position"": { ""type"": ""object"" },
                        ""volume"": { ""type"": ""number"" },
                        ""loop"": { ""type"": ""boolean"" },
                        ""auto_play"": { ""type"": ""boolean"" }
                    },
                    ""required"": [""name"", ""spec""]
                }"),
                HandleCreateMusic);

            Register("add_primitive", ToolDocs.Describe("add_primitive"),
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""id"": { ""type"": ""string"", ""description"": ""primitive id (omit to list the catalog)"" },
                        ""path"": { ""type"": ""string"", ""description"": ""optional target file path (defaults per primitive)"" }
                    }
                }"),
                HandleAddPrimitive);

            Register("new_scene", ToolDocs.Describe("new_scene"),
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""two_d"": { ""type"": ""boolean"", ""description"": ""2D setup (default: true)"" },
                        ""save_path"": { ""type"": ""string"", ""description"": ""Optional engine asset path to save the new scene"" }
                    }
                }"),
                (args, ct) => _engine.ForwardToolCall(IpcConstants.Methods.NewScene, args, ct),
                new ToolAnnotations { DestructiveHint = true });

            Register("open_scene", ToolDocs.Describe("open_scene"),
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""path"": { ""type"": ""string"", ""description"": ""Engine asset path of the scene to open"" }
                    },
                    ""required"": [""path""]
                }"),
                (args, ct) => _engine.ForwardToolCall(IpcConstants.Methods.OpenScene, args, ct),
                new ToolAnnotations { DestructiveHint = true });

            Register("save_scene", ToolDocs.Describe("save_scene"),
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""path"": { ""type"": ""string"", ""description"": ""Optional engine asset path to save to (default: current path)"" }
                    }
                }"),
                (args, ct) => _engine.ForwardToolCall(IpcConstants.Methods.SaveScene, args, ct));

            Register("write_script", ToolDocs.Describe("write_script"),
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""path"": { ""type"": ""string"", ""description"": ""Path relative to the project root (or absolute)"" },
                        ""content"": { ""type"": ""string"", ""description"": ""Full file contents"" }
                    },
                    ""required"": [""path"", ""content""]
                }"),
                (args, ct) => _engine.ForwardToolCall(IpcConstants.Methods.WriteScript, args, ct));

            Register("list_platforms", ToolDocs.Describe("list_platforms"),
                ParseSchema(@"{ ""type"": ""object"", ""properties"": {} }"),
                (args, ct) => _engine.ForwardToolCall(IpcConstants.Methods.ListPlatforms, args, ct),
                new ToolAnnotations { ReadOnlyHint = true });

            Register("get_platform_settings", ToolDocs.Describe("get_platform_settings"),
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""platform"": { ""type"": ""string"", ""description"": ""Platform name (see list_platforms)"" }
                    },
                    ""required"": [""platform""]
                }"),
                (args, ct) => _engine.ForwardToolCall(IpcConstants.Methods.GetPlatformSettings, args, ct),
                new ToolAnnotations { ReadOnlyHint = true });

            Register("set_platform_settings", ToolDocs.Describe("set_platform_settings"),
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""platform"": { ""type"": ""string"", ""description"": ""Platform name (see list_platforms)"" },
                        ""settings"": { ""type"": ""object"", ""description"": ""Key-value settings to apply (keys from get_platform_settings)"" }
                    },
                    ""required"": [""platform"", ""settings""]
                }"),
                (args, ct) => _engine.ForwardToolCall(IpcConstants.Methods.SetPlatformSettings, args, ct));

            Register("switch_build_target", ToolDocs.Describe("switch_build_target"),
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""platform"": { ""type"": ""string"", ""description"": ""Platform to activate (see list_platforms)"" }
                    },
                    ""required"": [""platform""]
                }"),
                HandleSwitchBuildTarget,
                new ToolAnnotations { DestructiveHint = true });

            Register("build_player", ToolDocs.Describe("build_player"),
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

            Register("engine_status", ToolDocs.Describe("engine_status"),
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""engine"": { ""type"": ""string"", ""description"": ""Pin by engine ('unity'), pid or project name. Empty string unpins; omit to only report."" }
                    }
                }"),
                HandleEngineStatus,
                new ToolAnnotations { ReadOnlyHint = true });

            Register("list_windows", ToolDocs.Describe("list_windows"),
                ParseSchema(@"{ ""type"": ""object"", ""properties"": {} }"),
                HandleListWindows,
                new ToolAnnotations { ReadOnlyHint = true });

            Register("capture_window", ToolDocs.Describe("capture_window"),
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""title"": { ""type"": ""string"", ""description"": ""Substring of the target window's title (case-insensitive)"" }
                    },
                    ""required"": [""title""]
                }"),
                HandleCaptureWindow,
                new ToolAnnotations { ReadOnlyHint = true });

            Register("focus_window", ToolDocs.Describe("focus_window"),
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""title"": { ""type"": ""string"", ""description"": ""Substring of the target window's title (case-insensitive)"" }
                    },
                    ""required"": [""title""]
                }"),
                HandleFocusWindow);

            Register("ui_action", ToolDocs.Describe("ui_action"),
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""action"": {
                            ""type"": ""string"",
                            ""enum"": [""screenshot"", ""mouse_move"", ""left_click"", ""right_click"", ""middle_click"",
                                       ""double_click"", ""triple_click"", ""left_click_drag"", ""scroll"", ""type"", ""key"", ""wait""],
                            ""description"": ""What to do (omit when using 'steps')""
                        },
                        ""steps"": {
                            ""type"": ""array"",
                            ""description"": ""Up to 12 actions run in order, each shaped like this object; one screenshot at the end"",
                            ""items"": { ""type"": ""object"" }
                        },
                        ""x"": { ""type"": ""number"", ""description"": ""Target, in pixels of the last screenshot from its top-left"" },
                        ""y"": { ""type"": ""number"" },
                        ""to_x"": { ""type"": ""number"", ""description"": ""Drag destination, same units"" },
                        ""to_y"": { ""type"": ""number"" },
                        ""text"": { ""type"": ""string"", ""description"": ""For 'type': the text to enter"" },
                        ""keys"": { ""type"": ""string"", ""description"": ""For 'key': 'ctrl+s', 'alt+F4', 'Escape'"" },
                        ""scroll_direction"": { ""type"": ""string"", ""enum"": [""up"", ""down"", ""left"", ""right""] },
                        ""scroll_amount"": { ""type"": ""integer"", ""description"": ""Wheel notches (1-30, default 3)"" },
                        ""duration_ms"": { ""type"": ""integer"", ""description"": ""For 'wait' (default 500)"" },
                        ""window_title"": { ""type"": ""string"", ""description"": ""Title substring of the window to act on; sticks until changed"" },
                        ""settle_ms"": { ""type"": ""integer"", ""description"": ""Pause before the follow-up screenshot (default 250)"" },
                        ""screenshot"": { ""type"": ""boolean"", ""description"": ""Return the screenshot after the action (default true)"" }
                    }
                }"),
                HandleUiAction);

            // ---- Runtime loop: run the game, observe it, drive it ----

            Register("enter_play", ToolDocs.Describe("enter_play"),
                ParseSchema(@"{ ""type"": ""object"", ""properties"": {} }"),
                HandleEnterPlay);

            Register("exit_play", ToolDocs.Describe("exit_play"),
                ParseSchema(@"{ ""type"": ""object"", ""properties"": {} }"),
                HandleExitPlay);

            Register("set_play_pause", ToolDocs.Describe("set_play_pause"),
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""paused"": { ""type"": ""boolean"", ""description"": ""true = pause, false = resume (default: true)"" }
                    }
                }"),
                HandleSetPlayPause);

            Register("play_step", ToolDocs.Describe("play_step"),
                ParseSchema(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""frames"": { ""type"": ""integer"", ""description"": ""Frames to advance (default: 1)"" }
                    }
                }"),
                HandlePlayStep);

            Register("get_play_state", ToolDocs.Describe("get_play_state"),
                ParseSchema(@"{ ""type"": ""object"", ""properties"": {} }"),
                HandleGetPlayState,
                new ToolAnnotations { ReadOnlyHint = true });

            Register("capture_sequence", ToolDocs.Describe("capture_sequence"),
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

            Register("send_input", ToolDocs.Describe("send_input"),
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
                        ""window_title"": { ""type"": ""string"", ""description"": ""Title substring of the OS window to focus and send the input to, OS-level"" },
                        ""coords"": {
                            ""type"": ""string"",
                            ""enum"": [""screen"", ""image""],
                            ""description"": ""What mouse_move x/y mean: 'screen' (default) OS pixels, or 'image' pixels of the last capture_window screenshot""
                        }
                    },
                    ""required"": [""events""]
                }"),
                HandleSendInput);

            Register("sample_state", ToolDocs.Describe("sample_state"),
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

            Register("assert_state", ToolDocs.Describe("assert_state"),
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
                                    ""tolerance"": { ""type"": ""number"" },
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

            Register("playtest", ToolDocs.Describe("playtest"),
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

        private async Task<ToolResult> HandleFocusWindow(JsonElement? args, CancellationToken ct)
        {
            if (await _consent.Refuse("Focusing a window", ct) is { } sessionRefusal)
                return ToolResult.Error(sessionRefusal);

            var capture = PlatformScreenCapture.Current;
            if (capture == null)
                return ToolResult.Error(PlatformScreenCapture.UnsupportedPlatformMessage);

            string? title = null;
            if (args is JsonElement a && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("title", out var t))
                title = t.GetString();
            if (string.IsNullOrWhiteSpace(title))
                return ToolResult.Error("Missing required 'title' (window title substring).");

            var ok = capture.FocusWindowByTitle(title, out var err);
            return ok
                ? ToolResult.Text($"Brought window matching '{title}' to the foreground.")
                : ToolResult.Error(err ?? "Failed to focus the window.");
        }

        /// <summary>
        /// Who is answering, who else is running, and — optionally — who to stick to.
        /// <para>
        /// Pinning has to DROP the current connection, not just record a preference: the reconnect
        /// loop only picks a target when there is no channel, so a pin alone would be honoured "next
        /// time something breaks" — which is the kind of half-fix that looks like it works.
        /// </para>
        /// </summary>
        private async Task<ToolResult> HandleEngineStatus(JsonElement? args, CancellationToken ct)
        {
            string? requested = null;
            if (args is JsonElement a && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("engine", out var e))
                requested = e.GetString();

            string? switchedFrom = null;
            if (requested != null)
            {
                string? pin = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
                _engine.PinnedEngine = pin;

                var connected = _engine.ConnectedEngine;
                if (pin != null && connected != null && !connected.Is(pin))
                {
                    switchedFrom = connected.ToString();
                    _engine.Disconnect();
                }
                // Connect now rather than at the retry loop's next tick, which can be ten seconds away:
                // also when nothing was connected, which used to leave the caller with "call again in a
                // moment". TryDiscoverAndConnect keeps a connection that already matches.
                if (!_engine.IsConnected || switchedFrom != null)
                    await _engine.TryDiscoverAndConnect(ct).ConfigureAwait(false);
            }

            var available = EngineConnection.DiscoverEngines();
            var now = _engine.ConnectedEngine;
            var pinned = _engine.PinnedEngine;
            var matching = pinned != null ? available.FindAll(e => e.Is(pinned)) : available;

            var payload = new
            {
                connected = now == null ? null : new
                {
                    engine = now.Engine,
                    version = now.Version,
                    pid = now.Pid,
                    project = now.Project,
                    projectPath = now.ProjectPath,
                    protocolVersion = now.ProtocolVersion,
                    pipe = now.Pipe,
                },
                pinned,
                switchedFrom,
                available = available.ConvertAll(x => new
                {
                    engine = x.Engine,
                    version = x.Version,
                    pid = x.Pid,
                    project = x.Project,
                    protocolVersion = x.ProtocolVersion,
                }),
                note = now == null
                    ? (pinned != null
                        // "Not connected" and "not running" are different things, and pinning
                        // makes the difference visible: a target can be listed and still
                        // unconnected while its plugin restarts. Saying it is not running then
                        // sends someone off to start an editor that is already open.
                        ? (matching.Count > 0
                            ? $"Pinned to '{pinned}', which is running, but it did not answer yet (a domain reload, " +
                              "or the plugin is restarting). It is retried in the background; call engine_status again."
                            : $"Not connected: nothing matching '{pinned}' (engine, pid or project) is running. " +
                              "Start it, or unpin with an empty string.")
                        : "Not connected: no engine plugin is running.")
                    : matching.Count > 1
                        // Two editors of one engine: a name pin cannot choose between them, and a reconnect
                        // may land on the other one.
                        ? (pinned == null
                            ? "More than one engine is running and none is pinned: a reconnect (any script recompile) may switch the target. Pin by pid or project."
                            : $"'{pinned}' matches {matching.Count} running editors: a reconnect may switch between them. Pin by pid or project.")
                        : null,
            };

            return ToolResult.Text(JsonSerializer.Serialize(payload, _jsonOptions));
        }

        private async Task<ToolResult> HandleListWindows(JsonElement? args, CancellationToken ct)
        {
            // Consent is asked before capability: a refusal is the answer even where the
            // platform could have obliged. The titles of every window on the machine
            // describe the user, not the project.
            if (await _consent.Refuse("Listing windows", ct) is { } refusal)
                return ToolResult.Error(refusal);

            var capture = PlatformScreenCapture.Current;
            if (capture == null)
                return ToolResult.Error(PlatformScreenCapture.UnsupportedPlatformMessage);
            var json = JsonSerializer.Serialize(new { windows = capture.ListWindows() }, _jsonOptions);
            return ToolResult.Text(json);
        }

        private async Task<ToolResult> HandleCaptureWindow(JsonElement? args, CancellationToken ct)
        {
            if (await _consent.Refuse("Capturing a window", ct) is { } refusal)
                return ToolResult.Error(refusal);

            string? title = null;
            if (args is JsonElement a && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("title", out var t))
                title = t.GetString();
            if (string.IsNullOrWhiteSpace(title))
                return ToolResult.Error("Missing required 'title' (window title substring).");

            var (jpeg, anchor, note, error) = CaptureWindowAnchored(title!);
            if (jpeg == null)
                return ToolResult.Error(error ?? "Window capture failed.");

            var content = new List<ContentItem>();
            if (anchor != null) content.Add(ContentItem.FromText(anchor.Describe()));
            else if (note != null) content.Add(ContentItem.FromText(note));
            content.Add(ContentItem.FromImage(Convert.ToBase64String(jpeg), "image/jpeg"));
            return new ToolResult { Content = content };
        }

        /// <summary>
        /// Capture one OS window and remember what the delivered image's pixels mean on
        /// screen, so a later ui_action can be given coordinates read straight off it.
        ///
        /// The geometry is read before the capture, not after: taken afterwards it could
        /// describe a window the user has since dragged, and every coordinate derived from
        /// it would be wrong by the width of that drag with nothing to show for it. When
        /// the platform cannot report geometry the capture still succeeds — the image is
        /// worth having on its own — and only the anchor is missing.
        ///
        /// Consent is the caller's to check, not this method's: every path into it is already
        /// a tool that asked.
        /// </summary>
        private (byte[]? jpeg, CaptureAnchor? anchor, string? note, string? error) CaptureWindowAnchored(string title)
        {
            var capture = PlatformScreenCapture.Current;
            if (capture == null)
                return (null, null, null, PlatformScreenCapture.UnsupportedPlatformMessage);

            capture.TryGetWindowGeometry(title, out var geometry, out var geometryError);

            var raw = capture.CaptureWindowByTitle(title, out var err);
            if (raw == null)
                return (null, null, null, err ?? "Window capture failed.");

            NormalizedImage image;
            try { image = ImageProcessor.Normalize(raw); }
            catch (Exception ex) { return (null, null, null, $"Image processing failed: {ex.Message}"); }

            if (geometry == null)
            {
                return (image.Bytes, null, $"Window geometry unavailable ({geometryError}); " +
                                           "ui_action cannot map coordinates on this capture.", null);
            }

            var anchor = new CaptureAnchor
            {
                TitleQuery = title,
                WindowTitle = string.IsNullOrEmpty(geometry.Title) ? title : geometry.Title,
                OriginX = geometry.OriginX,
                OriginY = geometry.OriginY,
                // The window's own size, not the captured bitmap's. On Windows the two agree,
                // since the client rect is captured pixel for pixel. They need not agree
                // elsewhere — a Retina capture can come back at twice the size of the window
                // it shows — and it is the window's units the pointer moves in, so those are
                // the ones an image pixel has to be converted into.
                SourceWidth = geometry.Width,
                SourceHeight = geometry.Height,
                ImageWidth = image.Width,
                ImageHeight = image.Height,
            };
            _lastCapture = anchor;
            return (image.Bytes, anchor, null, null);
        }

        private static readonly string[] UiActions =
        {
            "screenshot", "mouse_move", "left_click", "right_click", "middle_click",
            "double_click", "triple_click", "left_click_drag", "scroll", "type", "key", "wait",
        };

        /// <summary>
        /// One turn of the observe-act-observe loop against a real user interface: the model
        /// reads a point off the last screenshot, names an action there, and gets the next
        /// screenshot back from the same call.
        ///
        /// Coordinates are pixels of the image the model was shown, never screen pixels. The
        /// conversion belongs here because only the server knows both halves of it — the
        /// scale the image was normalized by and where the window sits on the virtual
        /// screen — and a model asked to do that arithmetic itself has no way to tell a
        /// click that missed from a click that did nothing.
        ///
        /// Everything runs OS-level: the target is the editor's own interface, which no
        /// in-process engine simulator can reach.
        /// </summary>
        private async Task<ToolResult> HandleUiAction(JsonElement? args, CancellationToken ct)
        {
            // One step or a list of them. A list is not a convenience: a click, the text it
            // focuses and the Enter that commits it are one intention, and splitting them over
            // three round trips inserts a model turn — and a screenshot — into the middle of a
            // gesture that was never in doubt. The list ends with one image, of the result.
            var steps = ReadUiSteps(args, out var stepsError);
            if (stepsError != null) return ToolResult.Error(stepsError);

            var capture = PlatformScreenCapture.Current;
            if (capture == null)
                return ToolResult.Error(PlatformScreenCapture.UnsupportedPlatformMessage);

            bool anyActsOnScreen = false;
            foreach (var s in steps)
            {
                var a = ActionOf(s);
                if (a.Length == 0 || Array.IndexOf(UiActions, a) < 0)
                    return ToolResult.Error(
                        $"'action' must be one of: {string.Join(", ", UiActions)}." +
                        (a.Length == 0 ? "" : $" Got '{a}'."));
                if (ActsOnScreen(a)) anyActsOnScreen = true;
            }

            var injector = PlatformInput.Current;
            if (anyActsOnScreen && injector == null)
                return ToolResult.Error(PlatformInput.UnsupportedPlatformMessage);

            if (await _consent.Refuse("Driving the interface", ct) is { } refusal)
                return ToolResult.Error(refusal);

            // Which window this call is about: the one asked for, else the one already
            // anchored, else the running game's, else the editor's.
            var title = ReadString(args, "window_title");
            if (string.IsNullOrWhiteSpace(title)) title = _lastCapture?.TitleQuery;
            if (string.IsNullOrWhiteSpace(title)) title = _gameWindowTitle;
            if (string.IsNullOrWhiteSpace(title)) title = await GetEngineWindowTitle(ct);
            if (string.IsNullOrWhiteSpace(title))
                return ToolResult.Error(
                    "No window to act on. Pass 'window_title' (a title substring, as in list_windows).");

            var anchor = _lastCapture;
            if (anchor == null || !string.Equals(anchor.TitleQuery, title, StringComparison.OrdinalIgnoreCase))
            {
                // Nothing has been seen of this window yet, so there is no image whose pixels
                // the coordinates could refer to. Take one now: it costs a capture, and the
                // alternative is acting on coordinates that mean nothing.
                var (freshJpeg, fresh, note, err) = CaptureWindowAnchored(title!);
                if (fresh == null)
                    return ToolResult.Error(err ?? note ?? $"Cannot anchor to window '{title}'.");
                anchor = fresh;
                if (anyActsOnScreen)
                    // The picture goes back with the refusal: it is the one the coordinates must be read from, and
                    // without it the next turn was spent asking for exactly this screenshot.
                    return ImageResult(freshJpeg,
                        $"No screenshot of '{anchor.WindowTitle}' had been taken yet, so image coordinates had no " +
                        "meaning. It is anchored now: this is the screenshot — read the coordinates from it and " +
                        "repeat the action.\n" + anchor.Describe(), isError: true);
                // A call that only looks has just looked: the image taken to anchor is its answer, without the
                // settle and the second capture that followed.
                if (freshJpeg != null && steps.TrueForAll(s => ActionOf(s) == "screenshot")
                    && ReadBool(args ?? default, "screenshot", true))
                    return ImageResult(freshJpeg, anchor.Describe(), isError: false);
            }
            else if (!anyActsOnScreen && steps.TrueForAll(s => ActionOf(s) == "screenshot" && ReadNumber(s, "settle_ms") == null)
                     && ReadNumber(args, "settle_ms") == null && ReadBool(args ?? default, "screenshot", true))
            {
                // Only a look, at a window already anchored: nothing to settle before it, unless a settle was asked for.
                var (onlyJpeg, onlyAnchor, onlyNote, onlyErr) = CaptureWindowAnchored(title!);
                return onlyJpeg == null
                    ? ToolResult.Error($"Screenshot of '{title}' failed: {onlyErr}")
                    : ImageResult(onlyJpeg, onlyAnchor?.Describe() ?? onlyNote ?? "", isError: false);
            }

            var log = new List<string>();
            bool focused = false;
            ToolResult? failure = null;

            for (int i = 0; i < steps.Count && failure == null; i++)
            {
                var step = steps[i];
                var action = ActionOf(step);
                string label = steps.Count > 1 ? $"[{i + 1}] " : "";

                if (action == "wait")
                {
                    var waitMs = (int)Math.Clamp(ReadNumber(step, "duration_ms") ?? 500, 0, 10_000);
                    await Task.Delay(waitMs, ct);
                    log.Add($"{label}waited {waitMs} ms.");
                }
                else if (ActsOnScreen(action))
                {
                    // Synthetic input goes to the foreground window, so the target has to be
                    // it. Once per call: refocusing between steps would fight any menu or
                    // popup a previous step opened.
                    if (!focused)
                    {
                        if (!capture.FocusWindowByTitle(title!, out var focusErr))
                        {
                            failure = ToolResult.Error($"Cannot act on '{title}': {focusErr}");
                            break;
                        }
                        await Task.Delay(FocusSettleMs, ct);
                        focused = true;
                    }

                    var moved = FollowWindow(capture, title!, ref anchor, out var geometryError);
                    if (geometryError != null) { failure = ToolResult.Error(geometryError); break; }
                    if (moved != null) log.Add(moved);

                    var stepLog = new List<string>();
                    failure = PerformUiAction(action, step, anchor!, injector!, stepLog);
                    foreach (var line in stepLog) log.Add(label + char.ToLowerInvariant(line[0]) + line.Substring(1));
                }

                // A per-step pause, for the one step that opens something slow. The pause after
                // the last step is the settle before the screenshot, so it is not applied twice.
                var stepSettle = ReadNumber(step, "settle_ms");
                if (stepSettle.HasValue && i < steps.Count - 1)
                    await Task.Delay((int)Math.Clamp(stepSettle.Value, 0, 5_000), ct);
            }

            var lastStep = steps.Count > 0 ? steps[steps.Count - 1] : args;
            var settleMs = (int)Math.Clamp(
                ReadNumber(lastStep, "settle_ms") ?? ReadNumber(args, "settle_ms") ?? DefaultSettleMs, 0, 5_000);
            if (settleMs > 0) await Task.Delay(settleMs, ct);

            var report = string.Join(" ", log);
            if (failure != null)
            {
                // Still show where it got to: a sequence that stopped halfway is a picture
                // worth having, and the alternative is a blind retry.
                var reached = CaptureWindowAnchored(title!);
                var why = failure.Content.Count > 0 ? failure.Content[0].Text : "the step failed";
                var errorContent = new List<ContentItem>
                {
                    ContentItem.FromText((report.Length > 0 ? report + "\n" : "") + why)
                };
                if (reached.jpeg != null)
                    errorContent.Add(ContentItem.FromImage(Convert.ToBase64String(reached.jpeg), "image/jpeg"));
                return new ToolResult { Content = errorContent, IsError = true };
            }

            if (!ReadBool(args ?? default, "screenshot", true))
                return ToolResult.Text(report.Length > 0 ? report : "Done.");

            var (jpeg, after, afterNote, captureErr) = CaptureWindowAnchored(title!);
            if (jpeg == null)
            {
                // When nothing was done on screen the picture was the whole request, and
                // "done" without it is a success that delivered nothing.
                if (!anyActsOnScreen)
                    return ToolResult.Error($"Screenshot of '{title}' failed: {captureErr}");
                log.Add($"(the follow-up screenshot failed: {captureErr})");
                return ToolResult.Text(string.Join(" ", log));
            }

            var content = new List<ContentItem>
            {
                ContentItem.FromText(report + (report.Length > 0 ? "\n" : "")
                                     + (after?.Describe() ?? afterNote ?? ""))
            };
            content.Add(ContentItem.FromImage(Convert.ToBase64String(jpeg), "image/jpeg"));
            return new ToolResult { Content = content };
        }

        private static ToolResult ImageResult(byte[]? jpeg, string text, bool isError)
        {
            var content = new List<ContentItem> { ContentItem.FromText(text) };
            if (jpeg != null) content.Add(ContentItem.FromImage(Convert.ToBase64String(jpeg), "image/jpeg"));
            return new ToolResult { Content = content, IsError = isError };
        }

        private static string ActionOf(JsonElement? step)
            => (ReadString(step, "action") ?? "").Trim().ToLowerInvariant();

        private static bool ActsOnScreen(string action)
            => action != "screenshot" && action != "wait";

        /// <summary>
        /// The steps to run: an explicit 'steps' array, or the call itself read as one step.
        /// </summary>
        private static List<JsonElement?> ReadUiSteps(JsonElement? args, out string? error)
        {
            error = null;
            var steps = new List<JsonElement?>();

            if (args is JsonElement a && a.ValueKind == JsonValueKind.Object
                && a.TryGetProperty("steps", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                // Both at once is ambiguous, and the top-level action used to be dropped without a word.
                if (a.TryGetProperty("action", out var single) && single.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(single.GetString()))
                {
                    error = "Give either 'action' (one step) or 'steps' (a sequence), not both: put the action " +
                            "into 'steps' where it belongs in the order.";
                    return steps;
                }
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        error = "Each entry of 'steps' must be an object, e.g. " +
                                "{\"action\":\"left_click\",\"x\":100,\"y\":200}.";
                        return steps;
                    }
                    steps.Add(item);
                }
                if (steps.Count == 0)
                    error = "'steps' is empty. Give at least one step, or use 'action' for a single one.";
                if (steps.Count > MaxUiSteps)
                    error = $"'steps' has {steps.Count} entries; {MaxUiSteps} is the limit. A sequence " +
                            "longer than that is not a gesture any more — look at a screenshot in between.";
                return steps;
            }

            steps.Add(args);
            return steps;
        }

        /// <summary>
        /// Keeps the anchor pointing at the window after the user has touched it. A move only
        /// shifts the origin and the image still describes what is on screen, so it is followed
        /// silently; a resize re-lays out the contents, and every coordinate read off the old
        /// image now points at something else, so that one is refused rather than guessed at.
        /// </summary>
        private string? FollowWindow(
            IPlatformScreenCapture capture, string title, ref CaptureAnchor? anchor, out string? error)
        {
            error = null;
            if (anchor == null) return null;
            if (!capture.TryGetWindowGeometry(title, out var geometry, out _) || geometry == null) return null;

            if (geometry.Width != anchor.SourceWidth || geometry.Height != anchor.SourceHeight)
            {
                error = $"'{anchor.WindowTitle}' was resized since the last screenshot " +
                        $"({anchor.SourceWidth}x{anchor.SourceHeight} to {geometry.Width}x{geometry.Height}), " +
                        "so its layout no longer matches the image those coordinates came from. " +
                        "Call ui_action with action 'screenshot' and read the new one.";
                return null;
            }

            if (geometry.OriginX == anchor.OriginX && geometry.OriginY == anchor.OriginY) return null;

            anchor = anchor.WithOrigin(geometry.OriginX, geometry.OriginY);
            _lastCapture = anchor;
            return $"Window moved to {geometry.OriginX},{geometry.OriginY}; coordinates followed it.";
        }

        /// <summary>
        /// How many steps one call may carry. Past this the sequence is being used to navigate
        /// blind rather than to complete a gesture, and the screenshot at the end is too late
        /// to tell anyone where it went wrong.
        /// </summary>
        private const int MaxUiSteps = 12;

        /// <summary>Characters one 'type' action may send: a field's worth, about 15 s of typing.</summary>
        private const int MaxTypedChars = 500;

        /// <summary>
        /// Runs the pointer/keyboard half of ui_action. Returns null when it worked (the
        /// caller then takes the follow-up screenshot), or the error to hand back.
        /// </summary>
        private ToolResult? PerformUiAction(
            string action, JsonElement? args, CaptureAnchor anchor, IPlatformInput injector, List<string> log)
        {
            // A point is required by everything that aims at something; type and key take one
            // only to click into a field first.
            bool needsPoint = action != "type" && action != "key";
            var px = ReadNumber(args, "x");
            var py = ReadNumber(args, "y");

            int sx = 0, sy = 0;
            if (px.HasValue && py.HasValue)
            {
                if (!anchor.Contains(px.Value, py.Value))
                    return ToolResult.Error(
                        $"({px.Value:0.#}, {py.Value:0.#}) is outside the {anchor.ImageWidth}x{anchor.ImageHeight} " +
                        "image. Coordinates are pixels of the screenshot, measured from its top-left corner — " +
                        "not screen coordinates.");
                (sx, sy) = anchor.ToScreen(px.Value, py.Value);
            }
            else if (needsPoint)
            {
                return ToolResult.Error($"Action '{action}' needs 'x' and 'y' in image pixels.");
            }

            string where = px.HasValue ? $"({px!.Value:0.#}, {py!.Value:0.#}) -> screen {sx},{sy}" : "";
            string? error;

            switch (action)
            {
                case "mouse_move":
                    if (!injector.MoveTo(sx, sy, out error)) return ToolResult.Error(error!);
                    log.Add($"Moved to {where}.");
                    return null;

                case "left_click":
                case "right_click":
                case "middle_click":
                case "double_click":
                case "triple_click":
                {
                    var button = action == "right_click" ? "right" : action == "middle_click" ? "middle" : "left";
                    var count = action == "double_click" ? 2 : action == "triple_click" ? 3 : 1;
                    if (!injector.Click(sx, sy, button, count, out error)) return ToolResult.Error(error!);
                    log.Add($"{action} at {where}.");
                    return null;
                }

                case "left_click_drag":
                {
                    var tx = ReadNumber(args, "to_x");
                    var ty = ReadNumber(args, "to_y");
                    if (!tx.HasValue || !ty.HasValue)
                        return ToolResult.Error("left_click_drag needs 'to_x' and 'to_y' in image pixels.");
                    if (!anchor.Contains(tx.Value, ty.Value))
                        return ToolResult.Error(
                            $"The drag target ({tx.Value:0.#}, {ty.Value:0.#}) is outside the " +
                            $"{anchor.ImageWidth}x{anchor.ImageHeight} image.");
                    var (dx, dy) = anchor.ToScreen(tx.Value, ty.Value);
                    if (!injector.Drag(sx, sy, dx, dy, "left", out error)) return ToolResult.Error(error!);
                    log.Add($"Dragged {where} to ({tx.Value:0.#}, {ty.Value:0.#}) -> screen {dx},{dy}.");
                    return null;
                }

                case "scroll":
                {
                    var direction = (ReadString(args, "scroll_direction") ?? "down").ToLowerInvariant();
                    var amount = (int)Math.Clamp(ReadNumber(args, "scroll_amount") ?? 3, 1, 30);
                    bool horizontal = direction == "left" || direction == "right";
                    int clicks = direction switch
                    {
                        "up" => amount,
                        "down" => -amount,
                        "right" => amount,
                        "left" => -amount,
                        _ => 0,
                    };
                    if (clicks == 0)
                        return ToolResult.Error("'scroll_direction' must be up, down, left or right.");
                    if (!injector.Scroll(sx, sy, clicks, horizontal, out error)) return ToolResult.Error(error!);
                    log.Add($"Scrolled {direction} {amount} at {where}.");
                    return null;
                }

                case "type":
                {
                    var text = ReadString(args, "text");
                    if (string.IsNullOrEmpty(text))
                        return ToolResult.Error("Action 'type' needs 'text'.");
                    // Typed a character at a time and not stoppable once started: a long text
                    // keeps going into whatever window is in front after the call is abandoned.
                    if (text!.Length > MaxTypedChars)
                        return ToolResult.Error(
                            $"'text' is {text.Length} characters; {MaxTypedChars} is the limit for one 'type'. " +
                            "Longer content belongs in a file or a set_property, not in keystrokes.");
                    if (px.HasValue)
                    {
                        if (!injector.Click(sx, sy, "left", 1, out error)) return ToolResult.Error(error!);
                        log.Add($"Clicked {where} first.");
                    }
                    if (!injector.TypeText(text!, out error)) return ToolResult.Error(error!);
                    log.Add($"Typed {text!.Length} character(s).");
                    return null;
                }

                case "key":
                {
                    var keys = ReadString(args, "keys");
                    if (string.IsNullOrWhiteSpace(keys))
                        return ToolResult.Error("Action 'key' needs 'keys', e.g. 'ctrl+s' or 'Escape'.");
                    if (px.HasValue)
                    {
                        if (!injector.Click(sx, sy, "left", 1, out error)) return ToolResult.Error(error!);
                        log.Add($"Clicked {where} first.");
                    }
                    if (!injector.KeyChord(keys!, out error)) return ToolResult.Error(error!);
                    log.Add($"Sent '{keys}'.");
                    return null;
                }

                default:
                    return ToolResult.Error($"Action '{action}' is not implemented.");
            }
        }

        // How long to let the foreground change land before reading geometry and acting, and
        // how long to let the interface react before the follow-up screenshot. A menu that
        // opens on click needs the second one, or the image shows the frame before it opened.
        private const int FocusSettleMs = 120;
        private const int DefaultSettleMs = 250;

        private static double? ReadNumber(JsonElement? args, string name)
        {
            if (args is JsonElement a && a.ValueKind == JsonValueKind.Object
                && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                && v.TryGetDouble(out var d))
                return d;
            return null;
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

        private Task<ToolResult> HandleCreateSound(JsonElement? args, CancellationToken ct)
            => HandleCreateAudio(args, ct, SoundSynthesizer.RenderToWav, "sound-spec");

        private Task<ToolResult> HandleCreateMusic(JsonElement? args, CancellationToken ct)
        {
            // A looped clip is rendered to exactly its length, with the release tail folded onto
            // the start: a tail left at the end delays every downbeat after the first.
            bool loop = args is JsonElement a && a.ValueKind == JsonValueKind.Object
                        && a.TryGetProperty("loop", out var lp) && lp.ValueKind == JsonValueKind.True;
            // Async under the hood: the SoundFont engine may have to fetch its font. The fetch
            // runs in the background and a call waits for it at most 30 s, so the pipe is held
            // no longer than that. Chiptune never touches it.
            return HandleCreateAudio(args, ct, spec => MusicSequencer.RenderToWavAsync(spec, loop, ct)
                                                                     .GetAwaiter().GetResult(), "score");
        }

        /// <summary>
        /// Render a spec to WAV and hand it to the engine's importer. Shared by create_sound
        /// and create_music because everything except the renderer is the same: both produce
        /// mono WAV bytes and both ride the existing import_sound channel, so a whole second
        /// tool costs no protocol at all.
        /// </summary>
        private async Task<ToolResult> HandleCreateAudio(
            JsonElement? args, CancellationToken ct, Func<JsonElement, byte[]> render, string specName)
        {
            if (args is not JsonElement a || a.ValueKind != JsonValueKind.Object)
                return ToolResult.Error("Missing arguments.");

            if (!a.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(nameEl.GetString()))
                return ToolResult.Error("Missing required 'name'.");
            var name = nameEl.GetString()!;

            if (!a.TryGetProperty("spec", out var spec) || spec.ValueKind != JsonValueKind.Object)
                return ToolResult.Error($"Missing required 'spec' ({specName} object).");

            byte[] wav;
            try { wav = render(spec); }
            catch (Exception ex) { return ToolResult.Error($"Audio synthesis failed: {ex.Message}"); }

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

            // Which engine, from the connection itself, waiting out a reconnect first (add_primitive right after a
            // refresh_scripts lands in a Unity domain reload). It used to read the project info without waiting and,
            // finding nothing, assume Unity: a Godot editor that was reconnecting got Unity C# written into it.
            if (!_engine.IsConnected && !await _engine.WaitForConnection(20_000, ct).ConfigureAwait(false))
                return ToolResult.Error($"{EngineConnection.EngineDisconnectedPrefix} No engine connected, so there is no " +
                                        "engine to pick the primitive's variant for. Retry once the editor is up.");
            var engineKey = PrimitiveCatalog.EngineKey(_engine.ConnectedEngine?.Engine ?? "")
                            ?? PrimitiveCatalog.EngineKey(await _engine.ForwardResourceRead(IpcConstants.Methods.GetProjectInfo, ct));
            if (engineKey == null)
                return ToolResult.Error($"The connected engine ('{_engine.ConnectedEngine?.Engine}') has no primitives " +
                                        $"(available for: {string.Join(", ", prim.Variants.Keys)}).");
            if (!prim.Variants.TryGetValue(engineKey, out var source))
                return ToolResult.Error($"No '{id}' variant for engine '{engineKey}' yet (available for: {string.Join(", ", prim.Variants.Keys)}).");

            // An empty path is no path: the variant's default, not a write to "".
            var path = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("path", out var pEl)
                && pEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pEl.GetString())
                    ? pEl.GetString()!.Trim() : prim.DefaultFile;

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

        /// <summary>
        /// How long a play transition may take to show in get_play_state before it counts as failed. Settable so the
        /// loopback bench can prove the failure without waiting the real eight seconds.
        /// </summary>
        public TimeSpan PlaySettleTimeout { get; set; } = TimeSpan.FromSeconds(8);

        private async Task<ToolResult> HandlePlayTransition(string method, string label, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ToolResult result;
            try
            {
                result = await _engine.ForwardToolCall(method, null, ct, timeoutMs: 120_000);
            }
            finally
            {
                // On exit, clear any OS-level keys/buttons left held (an unbalanced pressed:true)
                // so a forgotten key-down can't stay physically stuck at the OS level. Before the
                // engine's answer is looked at, and in a finally: an exit that errors, is
                // cancelled or never reconnects is exactly when nobody else will release them.
                if (method == IpcConstants.Methods.ExitPlay)
                {
                    try { PlatformInput.Current?.ReleaseAll(); } catch { /* best-effort */ }
                }
            }

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

            // Settle: the engine's own enter/exit response can be OPTIMISTIC — Unity's
            // EnterPlaymode/ExitPlaymode are deferred, so the first result may predate the
            // transition. Poll get_play_state until it reflects the intended state (or a short
            // window elapses) and report the CONFIRMED state, not the optimistic snapshot.
            bool wantPlaying = method == IpcConstants.Methods.EnterPlay;
            var first = result.Content.Count > 0 ? result.Content[0].Text : null;
            if (ReadJsonBool(first, "supported", true) == false)
                // Answered as an error, not as "completed": playtest only stops on an error, and on an engine
                // without a play mode (Stride) it went on to run its assertions against the edit-mode scene and
                // could report PASS for a game that never ran.
                return ToolResult.Error(
                    $"This engine cannot {label} from the editor: " +
                    (ReadJsonString(first, "error") ?? ReadJsonString(first, "note") ?? "no play mode."));

            var state = result;
            bool? reached = null;
            // Eight seconds, not four: a play transition that reloads the domain (Unity) spends part of the window
            // reconnecting, and a slow editor reached the deadline while the transition was still under way.
            var deadline = DateTime.UtcNow + PlaySettleTimeout;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var polled = await _engine.ForwardToolCall(IpcConstants.Methods.GetPlayState, null, ct);
                if (!polled.IsError)
                {
                    state = polled;
                    var txt = polled.Content.Count > 0 ? polled.Content[0].Text : "";
                    var isPlaying = ReadJsonBoolNullable(txt, "isPlaying");
                    reached = isPlaying == wantPlaying;
                    if (reached == true) break;                          // reached the intended state
                    if (ReadJsonBool(txt, "supported", true) == false) break; // engine can't report it
                }
                await Task.Delay(200, ct).ConfigureAwait(false);
            }

            var settled = state.Content.Count > 0 ? state.Content[0].Text : null;
            if (reached == false && ReadJsonBool(settled, "supported", true) != false)
                // Compile errors keep Unity out of play; an engine refusing for its own reason does the same. Saying
                // "completed" here is what let a playtest run in edit mode.
                return ToolResult.Error(
                    $"Could not {label}: the engine still reports isPlaying={(!wantPlaying).ToString().ToLowerInvariant()} " +
                    $"after {(int)sw.Elapsed.TotalSeconds}s. " +
                    (wantPlaying ? "Check get_compile_errors and the console (get_console_logs) for why it did not start. " : "") +
                    $"State: {settled}");

            // Cache a separate-window game's title (Godot) so send_input/capture_sequence
            // auto-route to it without a manual window_title; clear it on exit.
            if (method == IpcConstants.Methods.EnterPlay)
                _gameWindowTitle = ReadJsonString(settled, "windowTitle");
            else if (method == IpcConstants.Methods.ExitPlay)
                _gameWindowTitle = null;

            var prefix = reloaded ? $"Engine reloaded ({sw.Elapsed.TotalSeconds:0.#}s). " : "";
            return ToolResult.Text($"{prefix}{label} completed.\n{settled}");
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
                //
                // Unless the engine was not failing but refusing. An adapter that gates the
                // agent's access returns no image on purpose, and it returns it the same way
                // an adapter with no capture at all does; falling back then hands over the
                // very picture the editor declined to take. So the policy is asked first, and
                // only here — the engine's own capture above already answered to it.
                if (await _consent.Refuse("Screenshot", ct) is { } refusal)
                    return (null, refusal);

                var capture = PlatformScreenCapture.Current;
                if (capture == null)
                    return (null,
                        engineResult.ErrorCode == IpcConstants.ErrorCodes.NotSupported
                            ? PlatformScreenCapture.UnsupportedPlatformMessage
                            : $"Screenshot failed: {engineResult.Error ?? "unknown error"}");

                StdioTransport.LogInfo(
                    $"Engine capture returned no image ({engineResult.ErrorCode ?? engineResult.Error ?? "null"}); using OS-level fallback.");

                // The game view of a game running in its own window (Godot) is that window, not the editor's: the
                // same routing capture_sequence and send_input already make after enter_play.
                if (!string.IsNullOrWhiteSpace(_gameWindowTitle)
                    && string.Equals(ReadString(args, "view"), "game", StringComparison.OrdinalIgnoreCase))
                    return CaptureWindowJpeg(_gameWindowTitle!);

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
            if (string.IsNullOrWhiteSpace(windowTitle)) windowTitle = _gameWindowTitle; // auto-route to the game window
            bool byWindow = !string.IsNullOrWhiteSpace(windowTitle);

            // Asked once for the whole sequence, and asked HERE rather than per frame, because
            // the window_title branch below goes straight to the OS: it does not pass through
            // the engine, so nothing else in this method would ever consult the policy. That
            // half was governed and this half was not, which is the same shape of hole the
            // consent gate exists to close.
            if (await _consent.Refuse("Capturing a sequence", ct) is { } refusal)
                return ToolResult.Error(refusal);

            var content = new List<ContentItem>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int captured = 0;
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
                captured++;
                content.Add(ContentItem.FromText($"frame {i + 1}/{count} @ ~{sw.Elapsed.TotalSeconds:0.0}s"));
                content.Add(ContentItem.FromImage(Convert.ToBase64String(jpeg), "image/jpeg"));
            }

            // Counted, not inferred from the content: a first frame that failed leaves its error text there, and
            // "no content" never happened — a sequence with no picture at all came back as a success.
            return new ToolResult { Content = content, IsError = captured == 0 };
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
            var coords = (ReadString(args, "coords") ?? "screen").Trim().ToLowerInvariant();
            if (coords != "screen" && coords != "image")
                return ToolResult.Error("'coords' must be 'screen' (the default) or 'image'.");

            // Image coordinates describe a screenshot of an OS window, so they only mean
            // something on the OS-level path: an engine injecting in-process puts the pointer
            // in its own view, where the same numbers land somewhere else entirely.
            //
            // An explicit window_title names the OS window the input is for, and the in-process
            // path can reach no window but the engine's own game view. It used to be tried
            // anyway: Unity took a click meant for one of the editor's floating windows, put it
            // in the Game View and answered success, and nothing happened where it was aimed.
            bool toNamedWindow = !string.IsNullOrWhiteSpace(ReadString(args, "window_title"));
            if (coords == "screen" && !toNamedWindow)
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
                else if (engineResult.Content.Count > 0
                         && engineResult.Content[0].Text?.Contains("(engine still connected)") == true)
                {
                    // Timed out, not refused: the in-process input is still queued behind a busy main thread and lands
                    // when it frees up. Typing the same keys at OS level now would deliver them twice.
                    return engineResult;
                }
            }

            // Strategy 2: OS-level fallback (focus the target window, then SendInput). Same
            // reasoning as the screenshot fallback: an engine that refused in-process injection
            // must not be typed at from outside instead.
            if (await _consent.Refuse("Sending input", ct) is { } inputRefusal)
                return ToolResult.Error(inputRefusal);

            var injector = PlatformInput.Current;
            if (injector == null)
                return ToolResult.Error(PlatformInput.UnsupportedPlatformMessage);

            var events = ParseInputEvents(args);
            if (events.Count == 0)
                return ToolResult.Error("No input events supplied. Provide an 'events' array, e.g. " +
                    "{ \"events\": [ { \"type\": \"key\", \"key\": \"Space\", \"hold_ms\": 60 } ] }.");
            if (events.Count > MaxInputEvents)
                return ToolResult.Error($"'events' has {events.Count} entries; {MaxInputEvents} is the limit per call. " +
                    "Input already sent cannot be called back, so a long script goes in several calls.");

            var anchor = _lastCapture;
            var namedTitle = ReadString(args, "window_title");
            if (coords == "image")
            {
                if (anchor == null)
                    return ToolResult.Error(
                        "coords:'image' needs a screenshot to refer to. Call capture_window (or " +
                        "ui_action with action 'screenshot') first, then give coordinates read off it.");
                // The anchor describes ONE window. Converting with it while focusing another puts
                // the pointer at the right offset inside the wrong rectangle, and says success.
                if (!string.IsNullOrWhiteSpace(namedTitle)
                    && !string.Equals(anchor.TitleQuery, namedTitle, StringComparison.OrdinalIgnoreCase))
                    return ToolResult.Error(
                        $"coords:'image' refers to the last screenshot, which is of '{anchor.WindowTitle}', not " +
                        $"'{namedTitle}'. Capture '{namedTitle}' first, then read the coordinates off that image.");
            }

            var capture = PlatformScreenCapture.Current;
            string? focusTitle = namedTitle;
            if (string.IsNullOrWhiteSpace(focusTitle) && coords == "image") focusTitle = anchor!.TitleQuery;
            if (string.IsNullOrWhiteSpace(focusTitle)) focusTitle = _gameWindowTitle; // auto-route to the game window
            if (string.IsNullOrWhiteSpace(focusTitle))
                focusTitle = await GetEngineWindowTitle(ct);

            // Synthetic input goes to whatever window is in front. Unless the target is in front,
            // it lands in the terminal, the IDE or the browser — a Delete meant for a dialog is a
            // Delete there. So no target, or a target that would not come forward, is a refusal.
            if (capture == null || string.IsNullOrWhiteSpace(focusTitle))
                return ToolResult.Error(
                    "No window to send the input to: the engine did not report one. Pass 'window_title' " +
                    "(a title substring, as list_windows shows it).");
            if (!capture.FocusWindowByTitle(focusTitle!, out var focusErr))
                return ToolResult.Error($"Input not sent: could not focus '{focusTitle}': {focusErr}");
            // The same pause ui_action makes: activation is asynchronous (on macOS it goes through System Events), and
            // the first events used to reach the window that was in front before.
            await Task.Delay(FocusSettleMs, ct).ConfigureAwait(false);
            var focusNote = $" (focused '{focusTitle}')";

            if (coords == "image")
            {
                // The same check ui_action makes: a moved window is followed, a resized one is
                // refused, since its layout no longer matches the image the numbers came from.
                var moved = FollowWindow(capture, focusTitle!, ref anchor, out var geometryError);
                if (geometryError != null) return ToolResult.Error(geometryError);
                if (moved != null) focusNote += $" {moved}";

                foreach (var ev in events)
                {
                    if (!string.Equals(ev.Type, "mouse_move", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!anchor!.Contains(ev.X, ev.Y))
                        return ToolResult.Error(
                            $"({ev.X:0.#}, {ev.Y:0.#}) is outside the {anchor.ImageWidth}x{anchor.ImageHeight} " +
                            "screenshot those coordinates are measured in.");
                    var (screenX, screenY) = anchor.ToScreen(ev.X, ev.Y);
                    ev.X = screenX;
                    ev.Y = screenY;
                }
            }

            if (!injector.Inject(events, out var dispatched, out var err))
                return ToolResult.Error($"OS-level input injection failed: {err}");

            var suffix = string.IsNullOrEmpty(err) ? "" : $" {err}";
            return ToolResult.Text(
                $"Injected {dispatched}/{events.Count} input event(s) via OS-level SendInput{focusNote}.{suffix}");
        }

        /// <summary>
        /// Events per send_input call. Each can hold for seconds and none can be called back once
        /// sent, so a runaway list keeps typing into whatever is in front long after anyone wanted it.
        /// </summary>
        private const int MaxInputEvents = 64;

        /// <summary>
        /// How long a key or button with neither 'pressed' nor 'hold_ms' is held: a tap. A bare
        /// event used to be a press with no release, and at OS level that key stays down for the
        /// whole desktop until exit_play — a stuck Ctrl turns the user's next keystrokes into shortcuts.
        /// </summary>
        private const double TapHoldMs = 40;

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

        // Returns a top-level JSON string property, or null if absent / not JSON / not a string.
        private static string? ReadJsonString(string? json, string prop)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var el = JsonSerializer.Deserialize<JsonElement>(json);
                if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v)
                    && v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    return string.IsNullOrWhiteSpace(s) ? null : s;
                }
            }
            catch { /* not JSON */ }
            return null;
        }

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
                bool hasPressed = e.TryGetProperty("pressed", out var pr)
                                  && (pr.ValueKind == JsonValueKind.True || pr.ValueKind == JsonValueKind.False);
                var holdMs = e.TryGetProperty("hold_ms", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetDouble() : 0;
                var ev = new Shared.Abstraction.InputEvent
                {
                    Type = e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? (t.GetString() ?? "key") : "key",
                    Key = e.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null,
                    Button = e.TryGetProperty("button", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null,
                    Action = e.TryGetProperty("action", out var ac) && ac.ValueKind == JsonValueKind.String ? ac.GetString() : null,
                    Pressed = !hasPressed || pr.GetBoolean(),
                    X = e.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0,
                    Y = e.TryGetProperty("y", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetDouble() : 0,
                    HoldMs = Math.Clamp(holdMs, 0, 5_000),
                };
                // Neither half asked for explicitly: a tap, never a press left hanging.
                bool pressable = !string.Equals(ev.Type, "mouse_move", StringComparison.OrdinalIgnoreCase)
                                 && !string.Equals(ev.Type, "action", StringComparison.OrdinalIgnoreCase);
                if (pressable && !hasPressed && ev.HoldMs <= 0) ev.HoldMs = TapHoldMs;
                list.Add(ev);
            }
            return list;
        }

        // ---- Runtime verification: read/assert live engine values via the execute host ----

        private async Task<ToolResult> HandleSampleState(JsonElement? args, CancellationToken ct)
        {
            if (args is not JsonElement a || !a.TryGetProperty("probes", out var probes)
                || probes.ValueKind != JsonValueKind.Object)
                return ToolResult.Error("Missing 'probes' object, e.g. { \"probes\": { \"y\": \"Find(\\\"Bird\\\").transform.position.y\" } }.");

            var samples = await SampleProbes(probes, ct);
            return ToolResult.Text(JsonSerializer.Serialize(new { samples }, _jsonOptions));
        }

        /// <summary>The probes of one object — {name: expression} — read together, as {name, value, error} entries.</summary>
        private async Task<List<object>> SampleProbes(JsonElement probes, CancellationToken ct)
        {
            var names = new List<string>();
            var exprs = new List<string?>();
            foreach (var p in probes.EnumerateObject())
            {
                names.Add(p.Name);
                exprs.Add(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : null);
            }

            var results = await SampleMany(exprs, ct);
            var samples = new List<object>();
            for (int i = 0; i < names.Count; i++)
                samples.Add(new { name = names[i], value = results[i].value, error = results[i].error });
            return samples;
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

            // Read once: what each assertion asks does not change between polls.
            var asked = items.ConvertAll(it => new
            {
                expr = it.TryGetProperty("expression", out var ex) && ex.ValueKind == JsonValueKind.String ? (ex.GetString() ?? "") : "",
                op = it.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.String ? (o.GetString() ?? "==") : "==",
                expected = it.TryGetProperty("value", out var vv) ? vv : (JsonElement?)null,
                tolerance = it.TryGetProperty("tolerance", out var tol) && tol.ValueKind == JsonValueKind.Number ? tol.GetDouble() : (double?)null,
                label = it.TryGetProperty("label", out var lb) && lb.ValueKind == JsonValueKind.String ? lb.GetString() : null,
            });

            while (true)
            {
                report = new List<object>();
                allPass = true;
                var observedNow = await SampleMany(asked.ConvertAll(a => (string?)a.expr), ct);
                for (int i = 0; i < asked.Count; i++)
                {
                    var it = asked[i];
                    var (val, err) = observedNow[i];
                    // An operator that does not exist is the assertion's error. It used to evaluate to false and
                    // keep polling to the timeout, which reads exactly like a game that never reached the state.
                    if (err == null && !KnownOps.Contains((it.op ?? "==").Trim().ToLowerInvariant()))
                        err = $"unknown op '{it.op}'; use one of {string.Join(", ", KnownOps)}";
                    bool pass = err == null && EvalAssertion(val, it.op ?? "==", it.expected, it.tolerance);
                    if (!pass) allPass = false;

                    report.Add(new
                    {
                        it.label,
                        expression = it.expr,
                        it.op,
                        it.expected,
                        observed = err ?? (val.HasValue ? val.Value.GetRawText() : "(null)"),
                        pass,
                        error = err,
                    });
                }
                // Nothing to wait for: polling cannot make an assertion pass that is wrong as written.
                if (asked.Exists(a => !KnownOps.Contains((a.op ?? "==").Trim().ToLowerInvariant())))
                    break;

                if (allPass || DateTime.UtcNow >= deadline || ct.IsCancellationRequested) break;
                await Task.Delay(pollMs, ct).ConfigureAwait(false);
            }

            return (allPass, report);
        }

        private static readonly string[] KnownOps = { "==", "!=", "<", "<=", ">", ">=", "approx", "truthy", "falsy" };

        // What separates the values of a batch, and marks one that threw. Control characters, so no value a probe
        // returns as text can be mistaken for either.
        private const string ProbeSeparator = "\u001e";
        private const string ProbeErrorMark = "\u0001";
        private const string ProbeNullMark = "\u0002";

        /// <summary>
        /// "Find(\"Bird\").transform.position.y;" reads as the expression it is. Models often end a probe with the `;`
        /// of a statement, and wrapped as `return (object)(expr;);` it was a compile error.
        /// </summary>
        private static string CleanProbe(string expr) => expr.Trim().TrimEnd(';').TrimEnd();

        /// <summary>
        /// Several probes, read in ONE execute: one compile instead of one per probe, and every value from the same
        /// frame — read one call each, x and y of the same object could come from different frames. A null entry is a
        /// probe that was not given as a string. If the batch does not compile, each probe is read on its own, so the
        /// compile error lands on the probe that caused it.
        /// </summary>
        private async Task<List<(JsonElement? value, string? error)>> SampleMany(IReadOnlyList<string?> exprs, CancellationToken ct)
        {
            var results = new List<(JsonElement? value, string? error)>();
            var live = new List<int>();
            for (int i = 0; i < exprs.Count; i++)
            {
                var e = exprs[i] == null ? null : CleanProbe(exprs[i]!);
                if (e == null) results.Add((null, "a probe is a C# expression, given as a string"));
                else if (e.Length == 0) results.Add((null, "empty expression"));
                else { results.Add((null, null)); live.Add(i); }
            }
            if (live.Count == 0) return results;
            if (live.Count == 1)
            {
                results[live[0]] = await SampleOne(exprs[live[0]]!, ct);
                return results;
            }

            var code = new System.Text.StringBuilder();
            code.Append("var __aker = new string[").Append(live.Count).Append("];\n");
            for (int k = 0; k < live.Count; k++)
            {
                // Each value as text in the invariant culture — what a single probe returns — so 0.5 never reads "0,5".
                code.Append("try { var __v = (object)(").Append(CleanProbe(exprs[live[k]]!)).Append("); __aker[").Append(k)
                    .Append("] = __v == null ? \"\\u0002\" : System.Convert.ToString(__v, System.Globalization.CultureInfo.InvariantCulture); } ")
                    .Append("catch (System.Exception __e) { __aker[").Append(k).Append("] = \"\\u0001\" + __e.GetType().Name + \": \" + __e.Message; }\n");
            }
            code.Append("return string.Join(\"\\u001e\", __aker);");

            var (joined, error) = await ExecuteProbeCode(code.ToString(), ct);
            var parts = error == null && joined is { ValueKind: JsonValueKind.String } s
                ? (s.GetString() ?? "").Split(ProbeSeparator)
                : null;
            if (parts == null || parts.Length != live.Count)
            {
                foreach (var i in live) results[i] = await SampleOne(exprs[i]!, ct);
                return results;
            }

            for (int k = 0; k < live.Count; k++)
                results[live[k]] = parts[k].StartsWith(ProbeErrorMark, StringComparison.Ordinal)
                    ? (null, parts[k].Substring(ProbeErrorMark.Length))
                    : parts[k] == ProbeNullMark
                        ? (JsonSerializer.SerializeToElement<object?>(null), null)
                        : (JsonSerializer.SerializeToElement(parts[k]), null);
            return results;
        }

        // Evaluate one C# expression in the engine via the execute host and return its JSON value.
        private Task<(JsonElement? value, string? error)> SampleOne(string expr, CancellationToken ct)
        {
            expr = CleanProbe(expr);
            if (expr.Length == 0) return Task.FromResult<(JsonElement?, string?)>((null, "empty expression"));
            return ExecuteProbeCode("return (object)(" + expr + ");", ct);
        }

        private async Task<(JsonElement? value, string? error)> ExecuteProbeCode(string code, CancellationToken ct)
        {
            var argsEl = JsonSerializer.SerializeToElement(new { code, timeout_ms = 3000 });
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

        /// <param name="tolerance">For approx: how far apart still counts as equal. Default 1e-3, which is tight for physics.</param>
        private static bool EvalAssertion(JsonElement? observed, string op, JsonElement? expected, double? tolerance = null)
        {
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
                    case "approx": return Math.Abs(av - bv) <= Math.Abs(tolerance ?? 1e-3);
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
            // Float, not Any: Any allows thousands separators, so "0,5" — a value formatted in a comma-decimal culture —
            // read as 5 and an assertion on 0.5 failed, or passed, on the wrong number.
            if (e.ValueKind == JsonValueKind.String
                && double.TryParse(e.GetString(), System.Globalization.NumberStyles.Float,
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
            if (hasArgs && ValidatePlaytestSteps(a) is { } invalid)
                return ToolResult.Error($"playtest: {invalid}"); // before play starts: nothing to undo

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

            bool exited = false;
            try
            {
                await RunPlaytestTimeline(a, hasArgs, content, log, assertionReports, samplesLog, sw,
                                          pass => overallPass &= pass, ct);
                if (doExit)
                {
                    var r = await HandlePlayTransition(IpcConstants.Methods.ExitPlay, "exit play mode", ct);
                    exited = true;
                    log.AppendLine($"[{sw.Elapsed.TotalSeconds:0.0}s] exit_play {(r.IsError ? "ERROR" : "ok")}");
                }
            }
            finally
            {
                // A step that threw, or a cancelled call, must not leave the game running with a
                // key held down at OS level: the next tool call would find both, and so would the
                // user's own keyboard. Its own deadline, since the call's token may be the one
                // that fired.
                if (doExit && !exited)
                {
                    try
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        await HandlePlayTransition(IpcConstants.Methods.ExitPlay, "exit play mode", cleanup.Token);
                    }
                    catch { /* best effort: the original failure is the one worth reporting */ }
                }
                try { PlatformInput.Current?.ReleaseAll(); } catch { /* best effort */ }
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

        // The steps and the final criteria of a playtest, between its enter_play and exit_play.
        private async Task RunPlaytestTimeline(
            JsonElement a, bool hasArgs, List<ContentItem> content, System.Text.StringBuilder log,
            List<object> assertionReports, List<object> samplesLog, System.Diagnostics.Stopwatch sw,
            Action<bool> record, CancellationToken ct)
        {
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
                        // An input that did not land means the run did not test what it describes: not a PASS.
                        if (r.IsError) record(false);
                    }
                    else if (step.TryGetProperty("capture", out var cap))
                    {
                        JsonElement? capArgs = cap.ValueKind == JsonValueKind.Object ? cap : (JsonElement?)null;
                        // Routed like capture_sequence: a separate-window game (Godot, Stride) is
                        // photographed in its own window, or the frames show the editor while the
                        // input went to the game.
                        var capTitle = ReadString(capArgs, "window_title");
                        if (string.IsNullOrWhiteSpace(capTitle)) capTitle = _gameWindowTitle;
                        byte[]? jpeg; string? err;
                        if (string.IsNullOrWhiteSpace(capTitle))
                            (jpeg, err) = await CaptureFrameJpeg(capArgs, ct);
                        else if (await _consent.Refuse("Capturing the game window", ct) is { } capRefusal)
                            (jpeg, err) = (null, capRefusal);
                        else
                            (jpeg, err) = CaptureWindowJpeg(capTitle!);
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
                        record(pass);
                        assertionReports.Add(new { step = idx, at = Math.Round(sw.Elapsed.TotalSeconds, 1), passed = pass, assertions = rep });
                        log.AppendLine($"[{sw.Elapsed.TotalSeconds:0.0}s] assert -> {(pass ? "PASS" : "FAIL")}");
                    }
                    else if (step.TryGetProperty("sample", out var sam) && sam.ValueKind == JsonValueKind.Object)
                    {
                        var vals = await SampleProbes(sam, ct);
                        samplesLog.Add(new { step = idx, at = Math.Round(sw.Elapsed.TotalSeconds, 1), values = vals });
                        log.AppendLine($"[{sw.Elapsed.TotalSeconds:0.0}s] sample ({vals.Count})");
                    }
                }
            }

            if (hasArgs && a.TryGetProperty("criteria", out var crit) && crit.ValueKind == JsonValueKind.Array)
            {
                var (pass, rep) = await RunAssertions(crit, ReadDim(a, "criteria_timeout_ms", 0), 200, ct);
                record(pass);
                assertionReports.Add(new { step = "criteria", passed = pass, assertions = rep });
                log.AppendLine($"[{sw.Elapsed.TotalSeconds:0.0}s] criteria -> {(pass ? "PASS" : "FAIL")}");
            }
        }

        private static readonly string[] PlaytestActions = { "wait_ms", "input", "capture", "assert", "sample" };
        private static readonly string[] PlaytestModifiers = { "timeout_ms", "poll_ms", "label" };
        private const int MaxPlaytestCaptures = 8;

        /// <summary>
        /// The timeline read whole before play starts. A step with two actions ran only the first and dropped the other
        /// without a word ({"input":…, "wait_ms":300} lost its input), a misspelt key ({"wait":500}) was skipped, and
        /// any number of captures went back as images — each a few hundred KB of the caller's context.
        /// </summary>
        private static string? ValidatePlaytestSteps(JsonElement a)
        {
            if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("steps", out var steps)) return null;
            if (steps.ValueKind != JsonValueKind.Array) return "'steps' must be an array of step objects.";

            int idx = 0, captures = 0;
            foreach (var step in steps.EnumerateArray())
            {
                idx++;
                if (step.ValueKind != JsonValueKind.Object)
                    return $"Step {idx} is not an object. Each step is one of: {string.Join(", ", PlaytestActions)}.";
                var actions = new List<string>();
                foreach (var p in step.EnumerateObject())
                {
                    if (Array.IndexOf(PlaytestActions, p.Name) >= 0) actions.Add(p.Name);
                    else if (Array.IndexOf(PlaytestModifiers, p.Name) < 0)
                        return $"Step {idx} has an unknown key '{p.Name}'. A step is one of: {string.Join(", ", PlaytestActions)}" +
                               $" (assert also takes {string.Join(", ", PlaytestModifiers)}).";
                }
                if (actions.Count != 1)
                    return actions.Count == 0
                        ? $"Step {idx} has no action. A step is one of: {string.Join(", ", PlaytestActions)}."
                        : $"Step {idx} has {actions.Count} actions ({string.Join(", ", actions)}); give each its own step, in order.";
                if (actions[0] == "capture" && ++captures > MaxPlaytestCaptures)
                    return $"More than {MaxPlaytestCaptures} capture steps: each frame costs the caller's context. " +
                           "Use fewer, or capture_sequence for a burst.";
            }
            return null;
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
                    InputSchema = StripSchemaTitles(inputSchema),
                    // The table is the one place the four hints are decided; the
                    // per-call argument only covers a tool the table does not know.
                    Annotations = ToolAnnotationTable.For(name, annotations)
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

        /// <summary>
        /// Drop the "title" strings a JSON schema may carry on the object and on each
        /// property: no client shows them and they ride in the context on every turn.
        /// A property that is itself named "title" (capture_window, focus_window) holds
        /// an object, not a string, and is left alone.
        /// </summary>
        private static JsonElement StripSchemaTitles(JsonElement schema)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(schema.GetRawText());
            StripTitles(node);
            return JsonSerializer.Deserialize<JsonElement>(node!.ToJsonString());
        }

        private static void StripTitles(System.Text.Json.Nodes.JsonNode? node)
        {
            if (node is System.Text.Json.Nodes.JsonObject obj)
            {
                if (obj["title"] is System.Text.Json.Nodes.JsonValue) obj.Remove("title");
                foreach (var kv in obj.ToList()) StripTitles(kv.Value);
            }
            else if (node is System.Text.Json.Nodes.JsonArray arr)
            {
                foreach (var item in arr) StripTitles(item);
            }
        }

        private class RegisteredTool
        {
            public ToolDefinition Definition { get; set; } = null!;
            public Func<JsonElement?, CancellationToken, Task<ToolResult>> Handler { get; set; } = null!;
        }
    }
}
