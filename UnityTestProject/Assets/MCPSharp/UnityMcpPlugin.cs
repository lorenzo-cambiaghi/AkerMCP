#if UNITY_EDITOR
using UnityEngine;
using MCPSharp.Client;
using MCPSharp.Shared.Abstraction;
using MCPSharp.Shared.Serialization;

namespace MCPSharp.Unity
{
    public class UnityMcpPlugin : EnginePluginBase
    {
        private static UnityMcpPlugin _instance;
        private UnityMainThreadDispatcher _dispatcher;
        private UnityCompilationSupport _compilationSupport;

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
        public string CurrentPipeName => Config.PipeName;

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
        protected override IEditorContext CreateEditorContext() => new UnityEditorContext();
        protected override IAssetManager CreateAssetManager() => null;
        protected override ICompilationSupport CreateCompilationSupport()
        {
            _compilationSupport = new UnityCompilationSupport();
            return _compilationSupport;
        }

        protected override void Log(string message) => Debug.Log($"[MCPSharp] {message}");
        protected override void LogError(string message) => Debug.LogError($"[MCPSharp] {message}");

        public new void Stop()
        {
            _dispatcher?.Unregister();
            _compilationSupport?.Unhook();
            base.Stop();
        }
    }
}
#endif
