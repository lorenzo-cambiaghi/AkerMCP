#if TOOLS
#nullable enable
using Godot;
using AkerMcp.Client;
using AkerMcp.Shared.Abstraction;
using AkerMcp.Shared.Serialization;

namespace AkerMcp.GodotAdapter
{
    /// <summary>
    /// Godot adapter. Wires the engine-agnostic <see cref="EnginePluginBase"/>
    /// to Godot-specific implementations of the abstraction interfaces.
    /// </summary>
    public class GodotMcpPlugin : EnginePluginBase
    {
        private static GodotMcpPlugin? _instance;
        private GodotMainThreadDispatcher? _dispatcher;
        private GodotEditorContext? _editorContext;

        public static GodotMcpPlugin Instance => _instance ??= new GodotMcpPlugin();

        public static bool IsRunning => _instance != null && _instance.Config.PipeName != null;
        public string? CurrentPipeName => Config.PipeName;

        protected override ISceneGraph CreateSceneGraph()
        {
            GodotTypeRegistration.Register(TypeRegistry.Instance);
            return new GodotSceneGraph();
        }

        protected override IEngineCapabilities CreateCapabilities() => new GodotCapabilities();

        protected override IMainThreadDispatcher CreateDispatcher()
        {
            _dispatcher = new GodotMainThreadDispatcher();
            return _dispatcher;
        }

        protected override IEditorContext? CreateEditorContext()
        {
            _editorContext = new GodotEditorContext();
            return _editorContext;
        }

        protected override ICompilationSupport? CreateCompilationSupport() => new GodotCompilationSupport();

        // _dispatcher is assigned by CreateDispatcher(), which the base evaluates
        // before this in the IpcRequestHandler constructor argument list.
        protected override ICodeExecutor? CreateCodeExecutor() => new GodotCodeExecutor(_dispatcher!);

        protected override IScreenCapture? CreateScreenCapture() => new GodotScreenCapture();

        protected override IBuildManager? CreateBuildManager() => new GodotBuildManager();

        protected override ISpriteImporter? CreateSpriteImporter() => new GodotSpriteImporter();

        protected override ISceneManager? CreateSceneManager() => new GodotSceneManager();

        protected override IPlayModeController? CreatePlayModeController() => new GodotPlayModeController();
        protected override ISoundImporter? CreateSoundImporter() => new GodotAudioImporter();

        // Godot has no hook that hands a plugin the editor's console, so get_console_logs only ever
        // sees what is appended to the context's buffer. These two printed and appended nothing: the
        // buffer stayed empty for the plugin's whole life, and get_console_logs answered "no entries"
        // even right after a pipe error had been printed.
        protected override void Log(string message)
        {
            GD.Print($"[AkerMcp] {message}");
            _editorContext?.Append(LogLevel.Info, message);
        }

        protected override void LogError(string message)
        {
            GD.PrintErr($"[AkerMcp] {message}");
            _editorContext?.Append(LogLevel.Error, message);
        }

        /// <summary>
        /// Adds a line to the buffer get_console_logs reads, for code that prints on its own (the
        /// `execute` globals). A no-op while the plugin is not running.
        /// </summary>
        internal static void AppendToConsole(LogLevel level, string message)
            => _instance?._editorContext?.Append(level, message);

        /// <summary>Drains the main-thread action queue. Called once per editor frame.</summary>
        public void Tick() => _dispatcher?.ProcessQueue();

        public new void Stop()
        {
            base.Stop();
            _instance = null;
        }
    }
}
#endif
