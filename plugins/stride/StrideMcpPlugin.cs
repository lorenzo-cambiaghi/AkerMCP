#nullable enable
using System.Windows.Threading;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Diagnostics;

namespace AkerMcp.StrideAdapter
{
    /// <summary>
    /// Game Studio entry point. Game Studio only discovers editor plugins that
    /// derive from <see cref="AssetsPlugin"/> and are registered in its plugin
    /// list, so this thin class is the hook. Because <see cref="AssetsPlugin"/>
    /// and AkerMcp's <c>EnginePluginBase</c> are both base classes (C# is
    /// single-inheritance), the actual IPC server lives in a composed
    /// <see cref="StrideEnginePlugin"/> that we start once a session opens.
    /// </summary>
    public sealed class StrideMcpPlugin : AssetsPlugin
    {
        private StrideEnginePlugin? _engine;

        public override void InitializePlugin(ILogger logger)
        {
            // Nothing to do until a session (project) is opened.
        }

        public override void RegisterPrimitiveTypes(System.Collections.Generic.ICollection<System.Type> primitiveTypes)
        {
            // No custom primitive types contributed by this plugin.
        }

        public override void InitializeSession(SessionViewModel session)
        {
            // Called on the WPF UI thread after a project is loaded. Capture that
            // dispatcher so scene access marshals back onto the editor thread, then
            // bring up the named-pipe server that the AkerMcp server connects to.
            _engine?.Stop();
            _engine = new StrideEnginePlugin(session, Dispatcher.CurrentDispatcher);
            _engine.Start();
        }
    }
}
