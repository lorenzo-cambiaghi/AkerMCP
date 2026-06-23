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

        public StrideEnginePlugin(SessionViewModel session, Dispatcher uiDispatcher)
        {
            _session = session;
            _uiDispatcher = uiDispatcher;
        }

        protected override ISceneGraph CreateSceneGraph() => new StrideSceneGraph(_session);
        protected override IEngineCapabilities CreateCapabilities() => new StrideCapabilities();
        protected override IMainThreadDispatcher CreateDispatcher() => new StrideMainThreadDispatcher(_uiDispatcher);

        // Milestone 1 (walking skeleton): no code execution / screenshot / build yet.
        // These optional capabilities are added once the integration is verified live.

        protected override void Log(string message)
            => System.Diagnostics.Debug.WriteLine($"[AkerMcp] {message}");

        protected override void LogError(string message)
            => System.Diagnostics.Debug.WriteLine($"[AkerMcp][ERROR] {message}");
    }
}
