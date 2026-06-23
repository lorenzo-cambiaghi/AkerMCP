#nullable enable
using System.Windows.Threading;
using AkerMcp.Client;
using AkerMcp.Shared.Abstraction;
using Stride.Core.Assets.Editor.ViewModel;

namespace AkerMcp.StrideAdapter
{
    /// <summary>
    /// Concrete <see cref="EnginePluginBase"/> for Stride. Owns the named-pipe
    /// server and wires the engine-neutral abstractions to Stride/Game Studio.
    /// Created and started by <see cref="StrideMcpPlugin"/> when a session opens.
    /// </summary>
    public sealed class StrideEnginePlugin : EnginePluginBase
    {
        private readonly SessionViewModel _session;
        private readonly Dispatcher _uiDispatcher;
        private IMainThreadDispatcher? _dispatcher;

        public StrideEnginePlugin(SessionViewModel session, Dispatcher uiDispatcher)
        {
            _session = session;
            _uiDispatcher = uiDispatcher;

            // Wire the scene bridge to this session. ISceneGraph calls (reads and
            // Quantum writes) are marshalled onto the editor thread by the IPC handlers.
            StrideSceneBridge.Session = _session;
            StrideSceneGraph.RootEntitiesProvider = StrideSceneBridge.GetRootEntities;
        }

        protected override ISceneGraph CreateSceneGraph() => new StrideSceneGraph(_session);
        protected override IEngineCapabilities CreateCapabilities() => new StrideCapabilities();
        protected override IMainThreadDispatcher CreateDispatcher() => _dispatcher = new StrideMainThreadDispatcher(_uiDispatcher);

        protected override IEditorContext? CreateEditorContext() => new StrideEditorContext(_session);
        protected override ICompilationSupport? CreateCompilationSupport() => new StrideCompilationSupport(_session);

        // CreateDispatcher runs before this in the base ctor argument list, so _dispatcher is set.
        protected override ICodeExecutor? CreateCodeExecutor() => new StrideCodeExecutor(_dispatcher!);

        // take_screenshot uses the server's OS-level window fallback (no IScreenCapture).
        // IBuildManager not implemented yet → build tools report NOT_SUPPORTED.

        protected override void Log(string message)
            => System.Diagnostics.Debug.WriteLine($"[AkerMcp] {message}");

        protected override void LogError(string message)
            => System.Diagnostics.Debug.WriteLine($"[AkerMcp][ERROR] {message}");
    }
}
