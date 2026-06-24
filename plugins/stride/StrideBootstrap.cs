#nullable enable
using Stride.Core.Assets.Editor.Services;

namespace AkerMcp.StrideAdapter
{
    /// <summary>
    /// Single, idempotent registration entry point for the AkerMcp Stride adapter.
    /// Both deployment paths converge here:
    /// <list type="bullet">
    ///   <item>binary install — the <c>DOTNET_STARTUP_HOOKS</c> bootstrap
    ///   (<c>AkerMcp.Stride.StartupHook</c>) calls this reflectively once Game
    ///   Studio's editor assembly is loaded;</item>
    ///   <item>source build — the loader patched into
    ///   <c>Stride.GameStudio/Program.cs</c> registers the same plugin.</item>
    /// </list>
    /// Registering merely adds <see cref="StrideMcpPlugin"/> to
    /// <see cref="AssetsPlugin.RegisteredPlugins"/>; Game Studio then drives
    /// <c>InitializeSession</c> when a project opens, which starts the IPC server.
    /// </summary>
    public static class StrideBootstrap
    {
        private static bool _registered;

        /// <summary>
        /// Registers <see cref="StrideMcpPlugin"/> if it is not already present.
        /// Safe to call multiple times and from multiple loaders.
        /// </summary>
        public static void Register()
        {
            if (_registered) return;

            foreach (var plugin in AssetsPlugin.RegisteredPlugins)
            {
                if (plugin is StrideMcpPlugin)
                {
                    _registered = true;
                    return;
                }
            }

            AssetsPlugin.RegisterPlugin(typeof(StrideMcpPlugin));
            _registered = true;
        }
    }
}
