using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AkerMcp.Client;
using AkerMcp.Shared.Abstraction;
using AkerMcp.Shared.Reflection;

namespace AkerMcp.Loopback
{
    /// <summary>
    /// A headless fake engine: the REAL EnginePluginBase pipe server wired to trivial in-memory
    /// capability impls. Lets the whole client/IPC/server stack + the runtime-loop tools be
    /// exercised end-to-end with no Unity/Godot/Stride editor.
    /// </summary>
    public sealed class FakeEnginePlugin : EnginePluginBase
    {
        // Cached so state + recorded input persist across a Stop()/Start() (a simulated reload).
        public readonly FakePlayModeController Play = new FakePlayModeController();
        public readonly FakeInputSimulator Input = new FakeInputSimulator();
        public readonly FakeAgentConsent Consent = new FakeAgentConsent();
        public readonly FakeEditorContext Editor = new FakeEditorContext();
        public readonly FakeCodeExecutor Executor = new FakeCodeExecutor();

        private readonly string _engineName;

        /// <param name="engineName">
        /// Drives the pipe name and the discovery lock file, so a test can start TWO of these and
        /// check WHICH one the server picks — the case where the target used to change by itself.
        /// </param>
        public FakeEnginePlugin(string engineName = "Loopback") => _engineName = engineName;

        protected override ISceneGraph CreateSceneGraph() => new FakeSceneGraph();
        protected override IEngineCapabilities CreateCapabilities() => new FakeCapabilities(_engineName);
        protected override IMainThreadDispatcher CreateDispatcher() => new FakeDispatcher();
        protected override IEditorContext? CreateEditorContext() => Editor;
        protected override ICodeExecutor? CreateCodeExecutor() => Executor;
        protected override IScreenCapture? CreateScreenCapture() => new FakeScreenCapture();
        protected override IPlayModeController? CreatePlayModeController() => Play;
        protected override IInputSimulator? CreateInputSimulator() => Input;
        protected override IAgentConsent? CreateAgentConsent() => Consent;

        protected override void Log(string message) => Console.Error.WriteLine("[fake] " + message);
        protected override void LogError(string message) => Console.Error.WriteLine("[fake][err] " + message);
    }

    /// <summary>
    /// An engine whose user can revoke the session, so the test can check that a refusal is
    /// honoured by the half of the system that never goes through the engine at all.
    /// </summary>
    public sealed class FakeAgentConsent : IAgentConsent
    {
        public volatile bool Granted = true;
        public string Reason = "the loopback test revoked the session";
        public int Calls;

        public ConsentState GetConsent()
        {
            System.Threading.Interlocked.Increment(ref Calls);
            return new ConsentState { Granted = Granted, Reason = Granted ? null : Reason };
        }
    }

    public sealed class FakeDispatcher : IMainThreadDispatcher
    {
        public Task<T> RunOnMainThread<T>(Func<T> action, CancellationToken ct = default)
        {
            try { return Task.FromResult(action()); }
            catch (Exception e) { return Task.FromException<T>(e); }
        }

        public Task RunOnMainThread(Action action, CancellationToken ct = default)
        {
            try { action(); return Task.CompletedTask; }
            catch (Exception e) { return Task.FromException(e); }
        }
    }

    public sealed class FakeCapabilities : IEngineCapabilities
    {
        private readonly string _name;

        public FakeCapabilities(string name = "Loopback") => _name = name;

        public string EngineName => _name;
        public string EngineVersion => "1.0-test";
        public bool SupportsHotReload => false;
        public bool SupportsCodeExecution => true;
        public IEnumerable<string> GetRegisteredTypeNames() => Array.Empty<string>();
        public Type? ResolveType(string typeName) => null;
        public void RegisterTypeAlias(string alias, Type type) { }
    }

    public sealed class FakeSceneGraph : ISceneGraph
    {
        public ISceneNode? GetNode(string path) => path == FakeProbeNode.ProbePath ? new FakeProbeNode() : null;
        public IEnumerable<ISceneNode> GetRootNodes() => Array.Empty<ISceneNode>();
        // Like an engine that ignores PropertyFilter (none implements it): the probe matches every other filter.
        public IEnumerable<ISceneNode> Query(QueryFilter filter) => filter.MaxResults > 0 ? new ISceneNode[] { new FakeProbeNode() } : Array.Empty<ISceneNode>();
        public ISceneNode CreateNode(string type, string? name, string? parentPath)
            => throw new NotSupportedException("loopback scene graph is read-only");
        public bool DeleteNode(string path, bool recursive = true) => false;
        public int GetTotalNodeCount() => 0;
    }

    /// <summary>Shaped like UnityEngine.Vector3: public fields, plus a property of its own type (<c>normalized</c>).</summary>
    public struct FakeVector
    {
        public float x, y, z;
        public FakeVector(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public FakeVector normalized => this;
    }

    /// <summary>Shaped like System.DateTime: no public fields, only properties, one of its own type (<c>Date</c>).</summary>
    public struct FakeStamp
    {
        public long Ticks => 42;
        public FakeStamp Date => this;
    }

    /// <summary>Two property-only structs that return each other: a cycle no "own type" check sees.</summary>
    public struct FakeLoopA
    {
        public int Id => 7;
        public FakeLoopB Other => new FakeLoopB();
    }

    public struct FakeLoopB
    {
        public FakeLoopA Other => new FakeLoopA();
    }

    public enum FakeMode { Off, SolidColor }

    /// <summary>
    /// One node carrying the three value shapes an engine hands out as they are and that broke the wire: a vector whose
    /// property returns its own type, a property-only struct that does the same, and an enum.
    /// </summary>
    /// <summary>What set_property writes on the probe: plain fields, reached by the resolver the Unity node uses.</summary>
    public sealed class FakeProbeState
    {
        public FakeVector Position;
        public FakeMode Mode;
        public float Scale;
        public FakeAsset? Asset;
    }

    /// <summary>A reference type with a parameterless constructor, as many engine objects have.</summary>
    public sealed class FakeAsset
    {
        public string Name = "";
    }

    public sealed class FakeProbeNode : ISceneNode
    {
        public const string ProbePath = "/Probe";

        /// <summary>
        /// Static: the scene graph hands out a new node per lookup. The node's UnderlyingObject is the node itself, which
        /// has none of these members — as a Unity GameObject has no "position" — so the client cannot tell the member's
        /// type and passes the value's JSON text on.
        /// </summary>
        public static readonly FakeProbeState State = new FakeProbeState();

        public string Name => "Probe";
        public string Path => ProbePath;
        public string TypeName => "Probe";
        public ISceneNode? Parent => null;
        public IEnumerable<ISceneNode> Children => Array.Empty<ISceneNode>();
        public object UnderlyingObject => this;

        public object? GetProperty(string propertyPath) => propertyPath switch
        {
            "position" => new FakeVector(1, 2, 3),
            "mode" => FakeMode.SolidColor,
            "stamp" => new FakeStamp(),
            "loop" => new FakeLoopA(),
            _ => throw new KeyNotFoundException(propertyPath),
        };

        public void SetProperty(string propertyPath, object? value) => new PropertyPathResolver().Set(State, propertyPath, value);
        public object? CallMethod(string methodName, object?[]? args) => throw new NotSupportedException();
        public IEnumerable<ComponentInfo> GetComponents() => Array.Empty<ComponentInfo>();

        public IEnumerable<PropertyDescriptor> GetProperties()
        {
            yield return new PropertyDescriptor { Name = "name", TypeName = "string", CanRead = true, Value = Name };
            yield return new PropertyDescriptor { Name = "position", TypeName = "FakeVector", CanRead = true, Value = GetProperty("position") };
            yield return new PropertyDescriptor { Name = "mode", TypeName = "FakeMode", CanRead = true, Value = GetProperty("mode") };
            yield return new PropertyDescriptor { Name = "stamp", TypeName = "FakeStamp", CanRead = true, Value = GetProperty("stamp") };
        }

        public IEnumerable<MethodDescriptor> GetMethods() => Array.Empty<MethodDescriptor>();
    }

    public sealed class FakeEditorContext : IEditorContext
    {
        public bool IsEditorMode => true;
        public string? SelectedPath;
        public string? GetSelectedObjectPath() => SelectedPath;
        public void SetSelection(string objectPath) { }
        public string? GetCurrentScenePath() => "loopback://scene";
        public void OpenScene(string path) { }
        public void SaveScene() { }
        public string GetProjectPath() => System.IO.Path.GetTempPath();
        // The console buffer, newest last, filled by a test. Like the real plugins, GetRecentLogs hands out a copy of the tail.
        public readonly List<LogEntry> Logs = new List<LogEntry>();
        public IEnumerable<LogEntry> GetRecentLogs(int count = 50) => Logs.Skip(Math.Max(0, Logs.Count - count)).ToList();
        public void Log(string message, LogLevel level = LogLevel.Info) { }
    }

    // execute always "returns" the numeric string "1" — enough to drive sample_state/assert_state. DelayMs stands for a
    // script that takes a while, so a test can check who gives up waiting first.
    public sealed class FakeCodeExecutor : ICodeExecutor
    {
        public int DelayMs;

        public async Task<CodeExecutionResult> Execute(string code, int timeoutMs = 5000, CancellationToken ct = default)
        {
            if (DelayMs > 0) await Task.Delay(DelayMs, ct);
            return new CodeExecutionResult { Success = true, ReturnValue = "1", ElapsedMs = DelayMs };
        }
    }

    public sealed class FakePlayModeController : IPlayModeController
    {
        private bool _playing, _paused;
        private double _time;
        private long _frames;

        public PlayState GetState() => Snap();
        public PlayState EnterPlay() { _playing = true; _paused = false; _time = 0.016; _frames = 1; return Snap(); }
        public PlayState ExitPlay() { _playing = false; _paused = false; _time = 0; _frames = 0; return Snap(); }
        public PlayState SetPaused(bool paused) { _paused = paused; return Snap(); }
        public PlayState Step(int frames) { var n = Math.Max(1, frames); _time += 0.016 * n; _frames += n; return Snap(); }

        // Advance a frame per read while playing, so two get_play_state reads show a live loop.
        private PlayState Snap()
        {
            if (_playing && !_paused) _frames++;
            return new PlayState { IsPlaying = _playing, IsPaused = _paused, Time = _time, FrameCount = _frames, Fps = 60 };
        }
    }

    public sealed class FakeInputSimulator : IInputSimulator
    {
        public readonly List<InputEvent> Received = new List<InputEvent>();

        public InputResult SendInput(IReadOnlyList<InputEvent> events)
        {
            Received.AddRange(events);
            return new InputResult { Supported = true, Success = true, Dispatched = events.Count };
        }
    }

    // Returns a real 16x16 PNG (built with ImageSharp, the same lib the server decodes with)
    // so the server's ImageProcessor.NormalizeToJpeg can decode + re-encode it.
    public sealed class FakeScreenCapture : IScreenCapture
    {
        private static readonly byte[] Png = MakePng();

        public (byte[] bytes, string contentType)? CaptureView(string viewType) => (Png, "image/png");

        private static byte[] MakePng()
        {
            using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(
                16, 16, new SixLabors.ImageSharp.PixelFormats.Rgba32(200, 60, 60, 255));
            using var ms = new System.IO.MemoryStream();
            img.Save(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
            return ms.ToArray();
        }
    }
}
