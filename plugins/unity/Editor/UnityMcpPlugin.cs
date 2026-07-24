#nullable enable

using UnityEngine;
using AkerMcp.Client;
using AkerMcp.Shared.Abstraction;
using AkerMcp.Shared.Serialization;

namespace AkerMcp.Unity
{
    public class UnityMcpPlugin : EnginePluginBase
    {
        private static UnityMcpPlugin? _instance;
        private UnityMainThreadDispatcher? _dispatcher;
        private UnityCompilationSupport? _compilationSupport;
        private DynamicEvaluatorV2? _codeExecutor;

        public static UnityMcpPlugin Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new UnityMcpPlugin();
                return _instance;
            }
        }

        public static bool IsRunning => _instance != null && _instance.Config.PipeName != null;
        public string? CurrentPipeName => Config.PipeName;

        protected override ISceneGraph CreateSceneGraph()
        {
            UnityTypeRegistration.Register(TypeRegistry.Instance);
            return new UnitySceneGraph();
        }
        protected override IEngineCapabilities CreateCapabilities() => new UnityCapabilities();
        protected override IMainThreadDispatcher CreateDispatcher()
        {
            _dispatcher = new UnityMainThreadDispatcher();
            return _dispatcher;
        }
        protected override IEditorContext? CreateEditorContext() => new UnityEditorContext();
        protected override IAssetManager? CreateAssetManager() => null;
        protected override ICompilationSupport? CreateCompilationSupport()
        {
            _compilationSupport = new UnityCompilationSupport();
            return _compilationSupport;
        }
        protected override ICodeExecutor? CreateCodeExecutor()
        {
            _codeExecutor = new DynamicEvaluatorV2(_dispatcher!);
            return _codeExecutor;
        }
        protected override IScreenCapture? CreateScreenCapture() => new UnityScreenCapture();
        protected override IBuildManager? CreateBuildManager() => new UnityBuildManager();
        protected override ISpriteImporter? CreateSpriteImporter() => new UnitySpriteImporter();
        protected override ISceneManager? CreateSceneManager() => new UnitySceneManager();
        protected override IPlayModeController? CreatePlayModeController() => new UnityPlayModeController();
        // In-process input via the new Input System (reflection; falls back to OS-level if absent).
        protected override IInputSimulator? CreateInputSimulator() => new UnityInputSimulator(_dispatcher!);
        protected override ISoundImporter? CreateSoundImporter() => new UnityAudioImporter();

        protected override void Log(string message) => Debug.Log($"[AkerMcp] {message}");
        protected override void LogError(string message) => Debug.LogError($"[AkerMcp] {message}");

        public new void Stop()
        {
            _dispatcher?.Unregister();
            _compilationSupport?.Unhook();
            base.Stop();
            _instance = null;
        }
    }
}
