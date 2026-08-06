using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AkerMcp.Shared.Abstraction;
using AkerMcp.Shared.Ipc;
using AkerMcp.Shared.Reflection;
using AkerMcp.Shared.Serialization;

namespace AkerMcp.Client
{
    public class IpcRequestHandler
    {
        private readonly ISceneGraph _sceneGraph;
        private readonly IAssetManager? _assetManager;
        private readonly IEditorContext? _editorContext;
        private readonly ICompilationSupport? _compilationSupport;
        private readonly ICodeExecutor? _codeExecutor;
        private readonly IScreenCapture? _screenCapture;
        private readonly IBuildManager? _buildManager;
        private readonly ISpriteImporter? _spriteImporter;
        private readonly ISceneManager? _sceneManager;
        private readonly IPlayModeController? _playModeController;
        private readonly IInputSimulator? _inputSimulator;
        private readonly ISoundImporter? _soundImporter;
        private readonly IEngineCapabilities _capabilities;
        private readonly IMainThreadDispatcher _dispatcher;
        private readonly PropertyPathResolver _pathResolver;
        private readonly ReflectionInspector _inspector;
        private readonly MethodInvoker _methodInvoker;
        private readonly GenericSerializer _serializer;
        private readonly ClientConfiguration _config;

        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        // For tool outputs where null fields are pure noise (e.g. execute results).
        private readonly JsonSerializerOptions _compactJsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public IpcRequestHandler(
            ISceneGraph sceneGraph,
            IEngineCapabilities capabilities,
            IMainThreadDispatcher dispatcher,
            ClientConfiguration config,
            IAssetManager? assetManager = null,
            IEditorContext? editorContext = null,
            ICompilationSupport? compilationSupport = null,
            ICodeExecutor? codeExecutor = null,
            IScreenCapture? screenCapture = null,
            IBuildManager? buildManager = null,
            ISpriteImporter? spriteImporter = null,
            ISceneManager? sceneManager = null,
            IPlayModeController? playModeController = null,
            IInputSimulator? inputSimulator = null,
            ISoundImporter? soundImporter = null)
        {
            _sceneGraph = sceneGraph;
            _capabilities = capabilities;
            _dispatcher = dispatcher;
            _config = config;
            _assetManager = assetManager;
            _editorContext = editorContext;
            _compilationSupport = compilationSupport;
            _codeExecutor = codeExecutor;
            _screenCapture = screenCapture;
            _buildManager = buildManager;
            _spriteImporter = spriteImporter;
            _sceneManager = sceneManager;
            _playModeController = playModeController;
            _inputSimulator = inputSimulator;
            _soundImporter = soundImporter;
            _pathResolver = new PropertyPathResolver();
            _inspector = new ReflectionInspector();
            _methodInvoker = new MethodInvoker();
            _serializer = new GenericSerializer();
        }

        public async Task<IpcResponse> HandleRequest(IpcRequest request, CancellationToken ct)
        {
            try
            {
                // Binary response paths bypass the string-based switch
                if (request.Method == IpcConstants.Methods.TakeScreenshot)
                    return await HandleTakeScreenshot(request, ct);

                // Inbound-binary path: carries raw PNG bytes in request.Binary and
                // needs to report NOT_SUPPORTED when the engine lacks ISpriteImporter.
                if (request.Method == IpcConstants.Methods.ImportSprite)
                    return await HandleImportSprite(request, ct);

                if (request.Method == IpcConstants.Methods.ImportSound)
                    return await HandleImportSound(request, ct);

                var result = request.Method switch
                {
                    IpcConstants.Methods.Ping => "pong",
                    IpcConstants.Methods.Inspect => await HandleInspect(request, ct),
                    IpcConstants.Methods.GetProperty => await HandleGetProperty(request, ct),
                    IpcConstants.Methods.SetProperty => await HandleSetProperty(request, ct),
                    IpcConstants.Methods.CallMethod => await HandleCallMethod(request, ct),
                    IpcConstants.Methods.Query => await HandleQuery(request, ct),
                    IpcConstants.Methods.Create => await HandleCreate(request, ct),
                    IpcConstants.Methods.Delete => await HandleDelete(request, ct),
                    IpcConstants.Methods.GetSceneHierarchy => await HandleGetSceneHierarchy(ct),
                    IpcConstants.Methods.GetProjectInfo => HandleGetProjectInfo(),
                    IpcConstants.Methods.GetRecentLogs => HandleGetRecentLogs(),
                    IpcConstants.Methods.GetEngineTypes => HandleGetEngineTypes(),
                    IpcConstants.Methods.RefreshScripts => await HandleRefreshScripts(request, ct),
                    IpcConstants.Methods.GetCompileStatus => await HandleGetCompileStatus(ct),
                    IpcConstants.Methods.GetCompileErrors => await HandleGetCompileErrors(request, ct),
                    IpcConstants.Methods.GetConsoleLogs => HandleGetConsoleLogs(request),
                    IpcConstants.Methods.ClearConsole => HandleClearConsole(),
                    IpcConstants.Methods.SelectObject => await HandleSelectObject(request, ct),
                    IpcConstants.Methods.GetSelection => await HandleGetSelection(ct),
                    IpcConstants.Methods.Execute => await HandleExecute(request, ct),
                    IpcConstants.Methods.GetWindowInfo => HandleGetWindowInfo(),
                    IpcConstants.Methods.NewScene => await HandleNewScene(request, ct),
                    IpcConstants.Methods.OpenScene => await HandleOpenScene(request, ct),
                    IpcConstants.Methods.SaveScene => await HandleSaveScene(request, ct),
                    IpcConstants.Methods.WriteScript => await HandleWriteScript(request, ct),
                    IpcConstants.Methods.ListPlatforms => await HandleListPlatforms(ct),
                    IpcConstants.Methods.GetPlatformSettings => await HandleGetPlatformSettings(request, ct),
                    IpcConstants.Methods.SetPlatformSettings => await HandleSetPlatformSettings(request, ct),
                    IpcConstants.Methods.SwitchBuildTarget => await HandleSwitchBuildTarget(request, ct),
                    IpcConstants.Methods.BuildPlayer => await HandleBuildPlayer(request, ct),
                    IpcConstants.Methods.EnterPlay => await HandleEnterPlay(ct),
                    IpcConstants.Methods.ExitPlay => await HandleExitPlay(ct),
                    IpcConstants.Methods.SetPlayPause => await HandleSetPlayPause(request, ct),
                    IpcConstants.Methods.PlayStep => await HandlePlayStep(request, ct),
                    IpcConstants.Methods.GetPlayState => await HandleGetPlayState(ct),
                    IpcConstants.Methods.SendInput => await HandleSendInput(request, ct),
                    _ => throw new InvalidOperationException($"Unknown method: {request.Method}")
                };

                return IpcResponse.Ok(request.Id, Encoding.UTF8.GetBytes(result));
            }
            catch (Exception ex)
            {
                // Innermost frame only: a full stack trace costs hundreds of tokens
                // per failed call and the model can't act on engine internals anyway.
                var frame = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
                var detail = frame != null ? $" ({frame})" : "";
                return IpcResponse.Fail(request.Id, $"{ex.GetType().Name}: {ex.Message}{detail}");
            }
        }

        private async Task<IpcResponse> HandleTakeScreenshot(IpcRequest request, CancellationToken ct)
        {
            if (_screenCapture == null)
                return IpcResponse.FailWithCode(request.Id,
                    IpcConstants.ErrorCodes.NotSupported,
                    "Engine does not implement IScreenCapture.");

            var args = ParseArgs(request);
            var viewType = args.TryGetProperty("view", out var v)
                ? v.GetString() ?? "game" : "game";

            var captured = await _dispatcher.RunOnMainThread(
                () => _screenCapture.CaptureView(viewType), ct);

            if (captured == null)
                return IpcResponse.Fail(request.Id, "Engine capture returned null.");

            var (bytes, contentType) = captured.Value;
            if (bytes == null || bytes.Length == 0)
                return IpcResponse.Fail(request.Id, "Engine capture returned empty bytes.");

            return IpcResponse.Binary(request.Id, bytes, contentType);
        }

        private async Task<IpcResponse> HandleImportSprite(IpcRequest request, CancellationToken ct)
        {
            if (_spriteImporter == null)
                return IpcResponse.FailWithCode(request.Id,
                    IpcConstants.ErrorCodes.NotSupported,
                    "Engine does not implement ISpriteImporter.");

            if (request.Binary == null || request.Binary.Length == 0)
                return IpcResponse.Fail(request.Id, "import_sprite called without image bytes (request.Binary empty).");

            var args = ParseArgs(request);

            var importReq = new SpriteImportRequest
            {
                Name = args.TryGetProperty("name", out var n) ? (n.GetString() ?? "sprite") : "sprite",
                Png = request.Binary,
                PixelsPerUnit = args.TryGetProperty("pixels_per_unit", out var ppu) ? (float)ppu.GetDouble() : 100f,
                Filter = args.TryGetProperty("filter", out var f) ? (f.GetString() ?? "smooth") : "smooth",
                ScenePath = args.TryGetProperty("scene_path", out var sp) ? sp.GetString() : null,
            };

            if (args.TryGetProperty("pivot", out var pivot) && pivot.ValueKind == JsonValueKind.Object)
            {
                if (pivot.TryGetProperty("x", out var px)) importReq.PivotX = (float)px.GetDouble();
                if (pivot.TryGetProperty("y", out var py)) importReq.PivotY = (float)py.GetDouble();
            }

            if (args.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Object)
            {
                if (pos.TryGetProperty("x", out var x)) importReq.PosX = (float)x.GetDouble();
                if (pos.TryGetProperty("y", out var y)) importReq.PosY = (float)y.GetDouble();
                if (pos.TryGetProperty("z", out var z)) importReq.PosZ = (float)z.GetDouble();
            }

            var result = await _dispatcher.RunOnMainThread(
                () => _spriteImporter.ImportSprite(importReq), ct);

            return IpcResponse.Ok(request.Id,
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result, _compactJsonOptions)));
        }

        private async Task<IpcResponse> HandleImportSound(IpcRequest request, CancellationToken ct)
        {
            if (_soundImporter == null)
                return IpcResponse.FailWithCode(request.Id,
                    IpcConstants.ErrorCodes.NotSupported,
                    "Engine does not implement ISoundImporter.");

            if (request.Binary == null || request.Binary.Length == 0)
                return IpcResponse.Fail(request.Id, "import_sound called without WAV bytes (request.Binary empty).");

            var args = ParseArgs(request);

            var req = new SoundImportRequest
            {
                Name = args.TryGetProperty("name", out var n) ? (n.GetString() ?? "sound") : "sound",
                Wav = request.Binary,
                Volume = args.TryGetProperty("volume", out var vol) ? (float)vol.GetDouble() : 1f,
                Loop = args.TryGetProperty("loop", out var lp) && lp.GetBoolean(),
                AutoPlay = args.TryGetProperty("auto_play", out var ap) && ap.GetBoolean(),
                ScenePath = args.TryGetProperty("scene_path", out var sp) ? sp.GetString() : null,
            };

            if (args.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Object)
            {
                if (pos.TryGetProperty("x", out var x)) req.PosX = (float)x.GetDouble();
                if (pos.TryGetProperty("y", out var y)) req.PosY = (float)y.GetDouble();
                if (pos.TryGetProperty("z", out var z)) req.PosZ = (float)z.GetDouble();
            }

            var result = await _dispatcher.RunOnMainThread(
                () => _soundImporter.ImportSound(req), ct);

            return IpcResponse.Ok(request.Id,
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result, _compactJsonOptions)));
        }

        private string HandleGetWindowInfo()
        {
            var process = System.Diagnostics.Process.GetCurrentProcess();
            process.Refresh();

            var handle = process.MainWindowHandle.ToInt64();
            return JsonSerializer.Serialize(new
            {
                pid = process.Id,
                windowHandle = handle,
                windowTitle = process.MainWindowTitle,
                // Used by the macOS OS-level fallback to disambiguate the engine's
                // main window from its other windows (e.g. inspector palettes).
                windowTitlePrefix = _capabilities.EngineName
            }, _jsonOptions);
        }

        private async Task<string> HandleNewScene(IpcRequest request, CancellationToken ct)
        {
            if (_sceneManager == null) return ErrorJson("Scene management not available on this engine.");
            var args = ParseArgs(request);
            bool twoD = !args.TryGetProperty("two_d", out var t) || t.GetBoolean();
            string? savePath = args.TryGetProperty("save_path", out var sp) ? sp.GetString() : null;
            return await _dispatcher.RunOnMainThread(
                () => JsonSerializer.Serialize(_sceneManager.NewScene(twoD, savePath), _compactJsonOptions), ct);
        }

        private async Task<string> HandleOpenScene(IpcRequest request, CancellationToken ct)
        {
            if (_sceneManager == null) return ErrorJson("Scene management not available on this engine.");
            var args = ParseArgs(request);
            var path = args.GetProperty("path").GetString()!;
            return await _dispatcher.RunOnMainThread(
                () => JsonSerializer.Serialize(_sceneManager.OpenScene(path), _compactJsonOptions), ct);
        }

        private async Task<string> HandleSaveScene(IpcRequest request, CancellationToken ct)
        {
            if (_sceneManager == null) return ErrorJson("Scene management not available on this engine.");
            var args = ParseArgs(request);
            string? path = args.TryGetProperty("path", out var p) ? p.GetString() : null;
            return await _dispatcher.RunOnMainThread(
                () => JsonSerializer.Serialize(_sceneManager.SaveScene(path), _compactJsonOptions), ct);
        }

        private async Task<string> HandleWriteScript(IpcRequest request, CancellationToken ct)
        {
            if (_editorContext == null)
                return ErrorJson("Editor context not available; cannot resolve the project path.");

            var args = ParseArgs(request);
            var rel = args.GetProperty("path").GetString()!;
            var content = args.TryGetProperty("content", out var c) ? (c.GetString() ?? "") : "";

            return await _dispatcher.RunOnMainThread(() =>
            {
                // GetProjectPath returns a directory on Unity/Godot but the .sln file on
                // Stride — normalize to a directory either way.
                var root = _editorContext.GetProjectPath();
                if (System.IO.File.Exists(root))
                    root = System.IO.Path.GetDirectoryName(root) ?? root;

                var full = System.IO.Path.IsPathRooted(rel)
                    ? rel
                    : System.IO.Path.Combine(root, rel);

                var dir = System.IO.Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir))
                    System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(full, content);

                return JsonSerializer.Serialize(new { written = true, path = full }, _compactJsonOptions);
            }, ct);
        }

        private async Task<string> HandleInspect(IpcRequest request, CancellationToken ct)
        {
            var args = ParseArgs(request);
            var target = args.GetProperty("target").GetString()!;
            var depth = args.TryGetProperty("depth", out var d) ? d.GetInt32() : 1;
            var includeMethods = args.TryGetProperty("include_methods", out var im) && im.GetBoolean();
            var filter = args.TryGetProperty("filter", out var f) ? f.GetString() : null;

            depth = Math.Min(depth, _config.MaxInspectionDepth);

            return await _dispatcher.RunOnMainThread(() =>
            {
                var node = _sceneGraph.GetNode(target);
                if (node == null)
                {
                    var type = _capabilities.ResolveType(target);
                    if (type != null)
                    {
                        var typeResult = _inspector.InspectType(type, includeMethods, filter);
                        return CapOutput(JsonSerializer.Serialize(typeResult, _jsonOptions));
                    }
                    return ErrorJson($"Object not found: {target}");
                }

                // The node's DECLARED surface first (the default depth): what the engine says this
                // node IS — the same catalog list-verbs show and set_property/call_method accept.
                // Reflection over UnderlyingObject described a different dialect: raw CLR members
                // nobody declared, none of the declared semantics (units, ranges, sentences), and
                // for engines whose node wraps a richer surface (a component'd GameObject, a
                // catalog-backed document node) it missed the real one — an agent could CALL but
                // not DISCOVER. Depth > 1 stays reflective on purpose: nested expansion is a
                // power ask the declared surface does not model.
                if (depth == 1 && DeclaredSurface(node, includeMethods, filter) is { } declared)
                    return CapOutput(JsonSerializer.Serialize(declared, _jsonOptions));

                var result = _inspector.Inspect(node.UnderlyingObject, depth, includeMethods, filter);
                result.Path = node.Path;
                result.Components = node.GetComponents().ToList();
                result.ChildNames = node.Children.Select(c => c.Name).ToList();
                return CapOutput(JsonSerializer.Serialize(result, _jsonOptions));
            }, ct);
        }

        /// <summary>The inspection as the node's OWN declaration answers it, or null when the node
        /// declares nothing (or cannot answer) — then reflection is the only story there is. The
        /// filter matches like the reflective one (regex on names, case-insensitive); a filter that
        /// matches nothing still returns the (empty) declared result rather than silently switching
        /// dialect to reflection.</summary>
        private static InspectionResult? DeclaredSurface(ISceneNode node, bool includeMethods, string? filter)
        {
            try
            {
                var all = node.GetProperties().ToList();
                if (all.Count == 0) return null;
                bool Matches(string name) => filter == null ||
                    System.Text.RegularExpressions.Regex.IsMatch(name, filter,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var result = new InspectionResult
                {
                    TypeName = node.TypeName,
                    Path = node.Path,
                    Properties = all.Where(p => Matches(p.Name)).ToList(),
                    Components = node.GetComponents().ToList(),
                    ChildNames = node.Children.Select(c => c.Name).ToList(),
                };
                if (includeMethods)
                    result.Methods = node.GetMethods().Where(m => Matches(m.Name)).ToList();
                return result;
            }
            catch
            {
                return null; // a node that cannot answer must not fail inspect — fall back
            }
        }

        // Inspection of large objects/types can produce tens of thousands of tokens.
        // A truncated payload plus steering advice beats flooding the model context.
        private static string CapOutput(string json, int maxChars = 30_000)
        {
            if (json.Length <= maxChars) return json;
            return json.Substring(0, maxChars) +
                   $"\n…TRUNCATED ({json.Length} chars total). " +
                   "Narrow the inspection with 'filter' (regex on member names), lower 'depth', or omit 'include_methods'.";
        }

        private async Task<string> HandleGetProperty(IpcRequest request, CancellationToken ct)
        {
            var args = ParseArgs(request);
            var objectPath = args.GetProperty("object_path").GetString()!;
            var propertyPath = args.GetProperty("property_path").GetString()!;

            return await _dispatcher.RunOnMainThread(() =>
            {
                var node = _sceneGraph.GetNode(objectPath);
                if (node == null) return ErrorJson($"Object not found: {objectPath}");

                var value = node.GetProperty(propertyPath);
                var jsonValue = _serializer.ObjectToJsonElement(value);
                var result = new { path = objectPath, property = propertyPath, value = jsonValue };
                return JsonSerializer.Serialize(result, _jsonOptions);
            }, ct);
        }

        private async Task<string> HandleSetProperty(IpcRequest request, CancellationToken ct)
        {
            var args = ParseArgs(request);
            var objectPath = args.GetProperty("object_path").GetString()!;
            var propertyPath = args.GetProperty("property_path").GetString()!;
            var valueElement = args.GetProperty("value");

            return await _dispatcher.RunOnMainThread(() =>
            {
                var node = _sceneGraph.GetNode(objectPath);
                if (node == null) return $"Object not found: {objectPath}";

                var targetType = _pathResolver.GetTargetType(node.UnderlyingObject.GetType(), propertyPath);
                object? value = targetType != null
                    ? _serializer.JsonElementToObject(valueElement, targetType)
                    : valueElement.ToString();

                node.SetProperty(propertyPath, value);
                return $"Property '{propertyPath}' set successfully on {objectPath}";
            }, ct);
        }

        private async Task<string> HandleCallMethod(IpcRequest request, CancellationToken ct)
        {
            var args = ParseArgs(request);
            var target = args.GetProperty("target").GetString()!;
            var methodName = args.GetProperty("method").GetString()!;
            object?[]? methodArgs = null;

            if (args.TryGetProperty("args", out var argsArray) && argsArray.ValueKind == JsonValueKind.Array)
            {
                var list = new List<object?>();
                foreach (var item in argsArray.EnumerateArray())
                {
                    list.Add(item.ValueKind switch
                    {
                        JsonValueKind.String => item.GetString(),
                        JsonValueKind.Number => item.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null => null,
                        _ => item.GetRawText()
                    });
                }
                methodArgs = list.ToArray();
            }

            return await _dispatcher.RunOnMainThread(() =>
            {
                var node = _sceneGraph.GetNode(target);
                if (node != null)
                {
                    var result = node.CallMethod(methodName, methodArgs);
                    return result != null
                        ? JsonSerializer.Serialize(new { result = _serializer.ObjectToJsonElement(result) }, _jsonOptions)
                        : "{\"result\": null}";
                }

                var type = _capabilities.ResolveType(target);
                if (type != null)
                {
                    var result = _methodInvoker.InvokeStatic(type, methodName, methodArgs);
                    return result != null
                        ? JsonSerializer.Serialize(new { result = _serializer.ObjectToJsonElement(result) }, _jsonOptions)
                        : "{\"result\": null}";
                }

                return ErrorJson($"Target not found: {target}");
            }, ct);
        }

        private async Task<string> HandleQuery(IpcRequest request, CancellationToken ct)
        {
            var args = ParseArgs(request);
            var filter = new QueryFilter
            {
                TypeFilter = args.TryGetProperty("type_filter", out var tf) ? tf.GetString() : null,
                NamePattern = args.TryGetProperty("name_pattern", out var np) ? np.GetString() : null,
                Tag = args.TryGetProperty("tag", out var t) ? t.GetString() : null,
                MaxResults = args.TryGetProperty("max_results", out var mr) ? mr.GetInt32() : _config.MaxQueryResults
            };

            filter.MaxResults = Math.Min(filter.MaxResults, _config.MaxQueryResults);

            return await _dispatcher.RunOnMainThread(() =>
            {
                var results = _sceneGraph.Query(filter)
                    .Select(n => new { path = n.Path, type = n.TypeName, name = n.Name })
                    .ToList();
                return JsonSerializer.Serialize(results, _jsonOptions);
            }, ct);
        }

        private async Task<string> HandleCreate(IpcRequest request, CancellationToken ct)
        {
            var args = ParseArgs(request);
            var type = args.GetProperty("type").GetString()!;
            var name = args.TryGetProperty("name", out var n) ? n.GetString() : null;
            var parentPath = args.TryGetProperty("parent_path", out var pp) ? pp.GetString() : null;

            return await _dispatcher.RunOnMainThread(() =>
            {
                var node = _sceneGraph.CreateNode(type, name, parentPath);

                if (args.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in props.EnumerateObject())
                    {
                        try
                        {
                            var targetType = _pathResolver.GetTargetType(
                                node.UnderlyingObject.GetType(), prop.Name);
                            var value = targetType != null
                                ? _serializer.JsonElementToObject(prop.Value, targetType)
                                : prop.Value.ToString();
                            node.SetProperty(prop.Name, value);
                        }
                        catch
                        {
                            // Skip properties that can't be set during creation
                        }
                    }
                }

                return JsonSerializer.Serialize(new
                {
                    created = true,
                    path = node.Path,
                    type = node.TypeName,
                    name = node.Name
                }, _jsonOptions);
            }, ct);
        }

        private async Task<string> HandleDelete(IpcRequest request, CancellationToken ct)
        {
            var args = ParseArgs(request);
            var objectPath = args.GetProperty("object_path").GetString()!;
            var recursive = !args.TryGetProperty("recursive", out var r) || r.GetBoolean();

            return await _dispatcher.RunOnMainThread(() =>
            {
                var deleted = _sceneGraph.DeleteNode(objectPath, recursive);
                return JsonSerializer.Serialize(new { deleted, path = objectPath }, _jsonOptions);
            }, ct);
        }

        private async Task<string> HandleGetSceneHierarchy(CancellationToken ct)
        {
            return await _dispatcher.RunOnMainThread(() =>
            {
                var totalNodes = _sceneGraph.GetTotalNodeCount();
                var roots = _sceneGraph.GetRootNodes().ToList();

                if (totalNodes > 500)
                {
                    var typeGroups = new Dictionary<string, int>();
                    CountTypes(roots, typeGroups, 0, 3);
                    var types = string.Join(", ", typeGroups.Select(kv => $"{kv.Key}: {kv.Value}"));
                    return $"Scene: {totalNodes} nodes. Types: {types}.\nUse 'query' with filters to narrow down.";
                }

                return RenderTree(roots, 0);
            }, ct);
        }

        private string HandleGetProjectInfo()
        {
            var lines = new List<string>
            {
                $"Engine: {_capabilities.EngineName} {_capabilities.EngineVersion}"
            };

            if (_editorContext != null)
            {
                lines.Add($"Project: {_editorContext.GetProjectPath()}");
                lines.Add($"Scene: {_editorContext.GetCurrentScenePath() ?? "(none)"}");
                lines.Add($"Mode: {(_editorContext.IsEditorMode ? "Editor" : "Runtime")}");
            }

            return string.Join("\n", lines);
        }

        private string HandleGetRecentLogs()
        {
            if (_editorContext == null) return "(Editor context not available)";

            var logs = _editorContext.GetRecentLogs(50);
            return string.Join("\n", logs.Select(l =>
                $"[{l.Timestamp:HH:mm:ss}] [{l.Level}] {l.Message}"));
        }

        private string HandleGetEngineTypes()
        {
            return string.Join("\n", _capabilities.GetRegisteredTypeNames().Select(t => $"- {t}"));
        }

        private async Task<string> HandleRefreshScripts(IpcRequest request, CancellationToken ct)
        {
            if (_compilationSupport == null)
                return "{\"error\": \"Compilation support not available\"}";

            var args = ParseArgs(request);
            var waitForCompletion = !args.TryGetProperty("wait_for_completion", out var w) || w.GetBoolean();

            var before = await _dispatcher.RunOnMainThread(() =>
            {
                var status = _compilationSupport.GetCompilationStatus();
                _compilationSupport.RequestRecompile();
                return status;
            }, ct);

            if (!waitForCompletion)
                return "Recompilation requested (not waiting). Call get_compile_errors once the editor finishes.";

            // Compilation runs on the editor loop; poll from this background thread.
            // If the compile SUCCEEDS the engine does a domain reload and this
            // connection dies before a response can be sent — the MCP server detects
            // the drop, waits for reconnection and reports the outcome itself.
            // Completing this loop therefore means: errors (no reload happened),
            // or nothing to compile.
            var sawCompiling = false;
            var startGraceDeadline = DateTime.UtcNow.AddSeconds(10);
            var deadline = DateTime.UtcNow.AddSeconds(150);

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                var status = await _dispatcher.RunOnMainThread(
                    () => _compilationSupport.GetCompilationStatus(), ct);

                if (status.IsCompiling)
                {
                    sawCompiling = true;
                    continue;
                }

                if (status.IsImporting)
                {
                    // Asset pipeline workers still importing; compilation may start
                    // only once they finish — keep the no-op grace window open.
                    startGraceDeadline = DateTime.UtcNow.AddSeconds(10);
                    continue;
                }

                if (sawCompiling || status.LastCompileTime != before.LastCompileTime)
                    break; // a compile ran and finished without a domain reload

                if (DateTime.UtcNow > startGraceDeadline)
                    return "No script changes detected — nothing to compile. Compile state unchanged since " +
                           $"{before.LastCompileTime} ({(before.LastCompileSucceeded ? "SUCCESS" : "FAILED")}, " +
                           $"{before.ErrorCount} errors, {before.WarningCount} warnings).";
            }

            return await _dispatcher.RunOnMainThread(
                () => BuildCompileReport(_compilationSupport, errorsOnly: false), ct);
        }

        private static string BuildCompileReport(ICompilationSupport compilationSupport, bool errorsOnly)
        {
            const int maxWarnings = 10;

            var status = compilationSupport.GetCompilationStatus();
            var messages = compilationSupport.GetCompileMessages().ToList();
            var errors = messages.Where(m => m.Type == CompileMessageType.Error).ToList();
            var warnings = messages.Where(m => m.Type == CompileMessageType.Warning).ToList();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Compilation {(status.LastCompileSucceeded ? "SUCCEEDED" : "FAILED")} " +
                          $"at {status.LastCompileTime}: {errors.Count} error(s), {warnings.Count} warning(s).");

            foreach (var err in errors)
                sb.AppendLine(FormatCompileMessage(err, "error"));

            if (!errorsOnly && warnings.Count > 0)
            {
                foreach (var warn in warnings.Take(maxWarnings))
                    sb.AppendLine(FormatCompileMessage(warn, "warning"));
                if (warnings.Count > maxWarnings)
                    sb.AppendLine($"(+{warnings.Count - maxWarnings} more warnings)");
            }

            return sb.ToString().TrimEnd();
        }

        private static string FormatCompileMessage(CompileMessage m, string severity)
        {
            // Unity's CompilerMessage.message usually already embeds
            // "file(line,col): severity CSxxxx:" — don't print the location twice.
            if (!string.IsNullOrEmpty(m.File) &&
                m.Message.StartsWith(m.File, StringComparison.OrdinalIgnoreCase))
                return m.Message;
            return $"{m.File}({m.Line},{m.Column}): {severity}: {m.Message}";
        }

        private async Task<string> HandleGetCompileStatus(CancellationToken ct)
        {
            if (_compilationSupport == null)
                return "(Compilation support not available)";

            return await _dispatcher.RunOnMainThread(() =>
            {
                var status = _compilationSupport.GetCompilationStatus();
                return JsonSerializer.Serialize(status, _jsonOptions);
            }, ct);
        }

        private async Task<string> HandleGetCompileErrors(IpcRequest request, CancellationToken ct)
        {
            if (_compilationSupport == null)
                return "{\"error\": \"Compilation support not available\"}";

            var args = ParseArgs(request);
            var errorsOnly = args.TryGetProperty("errors_only", out var eo) && eo.GetBoolean();

            return await _dispatcher.RunOnMainThread(
                () => BuildCompileReport(_compilationSupport, errorsOnly), ct);
        }

        private string HandleGetConsoleLogs(IpcRequest request)
        {
            if (_editorContext == null) return "(Editor context not available)";

            var args = ParseArgs(request);
            var count = args.TryGetProperty("count", out var c) ? c.GetInt32() : 50;
            var levelFilter = args.TryGetProperty("level_filter", out var lf) ? lf.GetString() : "all";
            var search = args.TryGetProperty("search", out var s) ? s.GetString() : null;

            var logs = _editorContext.GetRecentLogs(count);

            if (levelFilter != null && levelFilter != "all")
            {
                LogLevel? filterLevel = levelFilter.ToLowerInvariant() switch
                {
                    "error" => LogLevel.Error,
                    "warning" => LogLevel.Warning,
                    "info" => LogLevel.Info,
                    "debug" => LogLevel.Debug,
                    _ => null
                };
                if (filterLevel.HasValue)
                    logs = logs.Where(l => l.Level == filterLevel.Value);
            }

            if (!string.IsNullOrEmpty(search))
                logs = logs.Where(l => l.Message.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);

            var entries = logs.ToList();
            if (entries.Count == 0)
                return "(No matching log entries)";

            var sb = new System.Text.StringBuilder();
            foreach (var entry in entries)
            {
                sb.Append($"[{entry.Timestamp:HH:mm:ss}] [{entry.Level}] {entry.Message}");
                if (entry.Level == LogLevel.Error && !string.IsNullOrEmpty(entry.StackTrace))
                    sb.Append($"\n  Stack: {entry.StackTrace.Split('\n')[0]}");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private string HandleClearConsole()
        {
            return "Console state noted. (Logs are retained in buffer for AI access)";
        }

        private async Task<string> HandleSelectObject(IpcRequest request, CancellationToken ct)
        {
            if (_editorContext == null)
                return "{\"error\": \"Editor context not available\"}";

            var args = ParseArgs(request);
            var objectPath = args.GetProperty("object_path").GetString()!;

            return await _dispatcher.RunOnMainThread(() =>
            {
                var node = _sceneGraph.GetNode(objectPath);
                if (node == null)
                    return ErrorJson($"Object not found: {objectPath}");

                _editorContext.SetSelection(objectPath);

                var components = node.GetComponents().ToList();
                var compList = string.Join(", ", components.Select(c => c.Name));

                return JsonSerializer.Serialize(new
                {
                    selected = true,
                    path = node.Path,
                    name = node.Name,
                    components = components
                }, _jsonOptions);
            }, ct);
        }

        private async Task<string> HandleGetSelection(CancellationToken ct)
        {
            if (_editorContext == null)
                return "{\"error\": \"Editor context not available\"}";

            return await _dispatcher.RunOnMainThread(() =>
            {
                var selectedPath = _editorContext.GetSelectedObjectPath();
                if (selectedPath == null)
                    return "{\"selected\": false, \"message\": \"Nothing selected\"}";

                var node = _sceneGraph.GetNode(selectedPath);
                if (node == null)
                    return "{\"selected\": false, \"message\": \"Selection is not a scene object\"}";

                const int MaxSelectionProperties = 20; // context economy: a fat node would flood the reply
                var components = node.GetComponents().ToList();
                var all = node.GetProperties().ToList();
                var properties = all.Take(MaxSelectionProperties).ToList();

                return JsonSerializer.Serialize(new
                {
                    selected = true,
                    path = node.Path,
                    name = node.Name,
                    type = node.TypeName,
                    components = components,
                    properties = properties,
                    // Silent truncation reads as "that is all there is" — say what was cut and how
                    // to reach it (the same courtesy the compile output pays its warnings).
                    truncatedProperties = all.Count > properties.Count
                        ? $"showing {properties.Count} of {all.Count} properties — read the rest by name with get_property, or use inspect"
                        : null,
                    childCount = node.Children.Count(),
                    childNames = node.Children.Select(c => c.Name).ToList()
                }, _jsonOptions);
            }, ct);
        }

        private async Task<string> HandleExecute(IpcRequest request, CancellationToken ct)
        {
            if (_codeExecutor == null)
                return JsonSerializer.Serialize(new { success = false, error = "Code execution not available. Engine plugin does not provide an ICodeExecutor." }, _jsonOptions);

            var args = ParseArgs(request);
            var code = args.GetProperty("code").GetString()!;
            var timeoutMs = args.TryGetProperty("timeout_ms", out var t) ? t.GetInt32() : 5000;

            var result = await _codeExecutor.Execute(code, timeoutMs, ct);

            return JsonSerializer.Serialize(new
            {
                success = result.Success,
                returnValue = result.ReturnValue,
                output = string.IsNullOrEmpty(result.Output) ? null : result.Output,
                error = string.IsNullOrEmpty(result.Error) ? null : result.Error,
                elapsedMs = Math.Round(result.ElapsedMs, 1)
            }, _compactJsonOptions);
        }

        private async Task<string> HandleListPlatforms(CancellationToken ct)
        {
            if (_buildManager == null) return BuildNotSupportedJson();
            return await _dispatcher.RunOnMainThread(() =>
                JsonSerializer.Serialize(new { platforms = _buildManager.GetPlatforms() }, _jsonOptions), ct);
        }

        private async Task<string> HandleGetPlatformSettings(IpcRequest request, CancellationToken ct)
        {
            if (_buildManager == null) return BuildNotSupportedJson();
            var args = ParseArgs(request);
            var platform = args.GetProperty("platform").GetString()!;
            return await _dispatcher.RunOnMainThread(() =>
                JsonSerializer.Serialize(_buildManager.GetPlatformSettings(platform), _compactJsonOptions), ct);
        }

        private async Task<string> HandleSetPlatformSettings(IpcRequest request, CancellationToken ct)
        {
            if (_buildManager == null) return BuildNotSupportedJson();
            var args = ParseArgs(request);
            var platform = args.GetProperty("platform").GetString()!;
            var values = new Dictionary<string, string>();
            if (args.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in s.EnumerateObject())
                    values[p.Name] = p.Value.ValueKind == JsonValueKind.String
                        ? (p.Value.GetString() ?? "")
                        : p.Value.GetRawText();
            }
            return await _dispatcher.RunOnMainThread(() =>
                JsonSerializer.Serialize(_buildManager.SetPlatformSettings(platform, values), _compactJsonOptions), ct);
        }

        private async Task<string> HandleSwitchBuildTarget(IpcRequest request, CancellationToken ct)
        {
            if (_buildManager == null) return BuildNotSupportedJson();
            var args = ParseArgs(request);
            var platform = args.GetProperty("platform").GetString()!;
            return await _dispatcher.RunOnMainThread(() =>
                JsonSerializer.Serialize(_buildManager.SwitchPlatform(platform), _compactJsonOptions), ct);
        }

        private async Task<string> HandleBuildPlayer(IpcRequest request, CancellationToken ct)
        {
            if (_buildManager == null) return BuildNotSupportedJson();
            var args = ParseArgs(request);
            var req = new BuildRequest
            {
                Platform = args.GetProperty("platform").GetString()!,
                OutputPath = args.GetProperty("output_path").GetString()!,
                Development = args.TryGetProperty("development", out var dev) && dev.GetBoolean(),
                Scenes = args.TryGetProperty("scenes", out var sc) && sc.ValueKind == JsonValueKind.Array
                    ? sc.EnumerateArray().Select(e => e.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                    : null
            };
            return await _dispatcher.RunOnMainThread(() =>
                JsonSerializer.Serialize(_buildManager.Build(req), _compactJsonOptions), ct);
        }

        private string BuildNotSupportedJson()
            => JsonSerializer.Serialize(new
            {
                error = "Platform/build operations not available. Engine plugin does not provide an IBuildManager."
            }, _jsonOptions);

        // ---- Runtime loop: play control (IPlayModeController) ----

        private async Task<string> HandleGetPlayState(CancellationToken ct)
        {
            if (_playModeController == null) return PlayNotSupportedJson();
            return await _dispatcher.RunOnMainThread(
                () => JsonSerializer.Serialize(_playModeController.GetState(), _compactJsonOptions), ct);
        }

        private async Task<string> HandleEnterPlay(CancellationToken ct)
        {
            if (_playModeController == null) return PlayNotSupportedJson();
            return await _dispatcher.RunOnMainThread(
                () => JsonSerializer.Serialize(_playModeController.EnterPlay(), _compactJsonOptions), ct);
        }

        private async Task<string> HandleExitPlay(CancellationToken ct)
        {
            if (_playModeController == null) return PlayNotSupportedJson();
            return await _dispatcher.RunOnMainThread(
                () => JsonSerializer.Serialize(_playModeController.ExitPlay(), _compactJsonOptions), ct);
        }

        private async Task<string> HandleSetPlayPause(IpcRequest request, CancellationToken ct)
        {
            if (_playModeController == null) return PlayNotSupportedJson();
            var args = ParseArgs(request);
            bool paused = !args.TryGetProperty("paused", out var p) || p.GetBoolean();
            return await _dispatcher.RunOnMainThread(
                () => JsonSerializer.Serialize(_playModeController.SetPaused(paused), _compactJsonOptions), ct);
        }

        private async Task<string> HandlePlayStep(IpcRequest request, CancellationToken ct)
        {
            if (_playModeController == null) return PlayNotSupportedJson();
            var args = ParseArgs(request);
            int frames = args.TryGetProperty("frames", out var f) && f.ValueKind == JsonValueKind.Number
                ? f.GetInt32() : 1;
            return await _dispatcher.RunOnMainThread(
                () => JsonSerializer.Serialize(_playModeController.Step(frames), _compactJsonOptions), ct);
        }

        private string PlayNotSupportedJson()
            => JsonSerializer.Serialize(new PlayState
            {
                Supported = false,
                Error = "Play mode control not available. Engine plugin does not provide an IPlayModeController."
            }, _compactJsonOptions);

        // ---- Runtime loop: input injection (IInputSimulator) ----
        // Returns InputResult JSON; Supported=false signals the server to try its
        // OS-level input fallback (mirrors the take_screenshot hybrid).

        private async Task<string> HandleSendInput(IpcRequest request, CancellationToken ct)
        {
            if (_inputSimulator == null)
                return JsonSerializer.Serialize(new InputResult
                {
                    Supported = false,
                    Error = "Engine plugin does not provide an IInputSimulator."
                }, _compactJsonOptions);

            var events = ParseInputEvents(ParseArgs(request));
            if (events.Count == 0)
                return JsonSerializer.Serialize(new InputResult
                {
                    Supported = true,
                    Success = false,
                    Error = "No input events supplied ('events' array is empty)."
                }, _compactJsonOptions);

            var result = await _dispatcher.RunOnMainThread(() => _inputSimulator.SendInput(events), ct);
            return JsonSerializer.Serialize(result, _compactJsonOptions);
        }

        private static List<InputEvent> ParseInputEvents(JsonElement args)
        {
            var list = new List<InputEvent>();
            if (!args.TryGetProperty("events", out var evs) || evs.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var e in evs.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var ev = new InputEvent
                {
                    Type = e.TryGetProperty("type", out var t) ? (t.GetString() ?? "key") : "key",
                    Key = e.TryGetProperty("key", out var k) ? k.GetString() : null,
                    Button = e.TryGetProperty("button", out var b) ? b.GetString() : null,
                    Action = e.TryGetProperty("action", out var a) ? a.GetString() : null,
                    Pressed = !e.TryGetProperty("pressed", out var pr) || pr.GetBoolean(),
                    X = e.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0,
                    Y = e.TryGetProperty("y", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetDouble() : 0,
                    HoldMs = e.TryGetProperty("hold_ms", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetDouble() : 0,
                };
                list.Add(ev);
            }
            return list;
        }

        // Error payloads must go through the serializer: paths/names coming from
        // the model can contain quotes or backslashes that would break
        // string-interpolated JSON.
        private string ErrorJson(string message)
            => JsonSerializer.Serialize(new { error = message }, _jsonOptions);

        private static JsonElement ParseArgs(IpcRequest request)
        {
            if (request.Payload == null || request.Payload.Length == 0)
                return JsonSerializer.Deserialize<JsonElement>("{}");
            return JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(request.Payload));
        }

        private static string RenderTree(IEnumerable<ISceneNode> nodes, int indent)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var node in nodes)
            {
                var pad = new string(' ', indent * 2);
                var components = node.GetComponents().ToList();
                var compList = string.Join(", ", components.Select(c => c.Name));
                sb.AppendLine($"{pad}{node.Name}  [{compList}]");
                foreach (var child in node.Children)
                {
                    sb.Append(RenderTree(new[] { child }, indent + 1));
                }
            }
            return sb.ToString();
        }

        private static void CountTypes(IEnumerable<ISceneNode> nodes,
            Dictionary<string, int> counts, int currentDepth, int maxDepth)
        {
            foreach (var node in nodes)
            {
                if (!counts.ContainsKey(node.TypeName))
                    counts[node.TypeName] = 0;
                counts[node.TypeName]++;

                if (currentDepth < maxDepth)
                    CountTypes(node.Children, counts, currentDepth + 1, maxDepth);
            }
        }
    }
}
