using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AkerMcp.Client;
using AkerMcp.Shared.Abstraction;

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

        private readonly string _engineName;

        /// <param name="engineName">
        /// Drives the pipe name and the discovery lock file, so a test can start TWO of these and
        /// check WHICH one the server picks — the case where the target used to change by itself.
        /// </param>
        public FakeEnginePlugin(string engineName = "Loopback") => _engineName = engineName;

        protected override ISceneGraph CreateSceneGraph() => new FakeSceneGraph();
        protected override IEngineCapabilities CreateCapabilities() => new FakeCapabilities(_engineName);
        protected override IMainThreadDispatcher CreateDispatcher() => new FakeDispatcher();
        protected override IEditorContext? CreateEditorContext() => new FakeEditorContext();
        protected override ICodeExecutor? CreateCodeExecutor() => new FakeCodeExecutor();
        protected override IScreenCapture? CreateScreenCapture() => new FakeScreenCapture();
        protected override IPlayModeController? CreatePlayModeController() => Play;
        protected override IInputSimulator? CreateInputSimulator() => Input;

        protected override void Log(string message) => Console.Error.WriteLine("[fake] " + message);
        protected override void LogError(string message) => Console.Error.WriteLine("[fake][err] " + message);
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
        public ISceneNode? GetNode(string path) => null;
        public IEnumerable<ISceneNode> GetRootNodes() => Array.Empty<ISceneNode>();
        public IEnumerable<ISceneNode> Query(QueryFilter filter) => Array.Empty<ISceneNode>();
        public ISceneNode CreateNode(string type, string? name, string? parentPath)
            => throw new NotSupportedException("loopback scene graph is read-only");
        public bool DeleteNode(string path, bool recursive = true) => false;
        public int GetTotalNodeCount() => 0;
    }

    public sealed class FakeEditorContext : IEditorContext
    {
        public bool IsEditorMode => true;
        public string? GetSelectedObjectPath() => null;
        public void SetSelection(string objectPath) { }
        public string? GetCurrentScenePath() => "loopback://scene";
        public void OpenScene(string path) { }
        public void SaveScene() { }
        public string GetProjectPath() => System.IO.Path.GetTempPath();
        public IEnumerable<LogEntry> GetRecentLogs(int count = 50) => Array.Empty<LogEntry>();
        public void Log(string message, LogLevel level = LogLevel.Info) { }
    }

    // execute always "returns" the numeric string "1" — enough to drive sample_state/assert_state.
    public sealed class FakeCodeExecutor : ICodeExecutor
    {
        public Task<CodeExecutionResult> Execute(string code, int timeoutMs = 5000, CancellationToken ct = default)
            => Task.FromResult(new CodeExecutionResult { Success = true, ReturnValue = "1", ElapsedMs = 0.1 });
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
