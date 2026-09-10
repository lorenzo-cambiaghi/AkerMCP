using System;
using System.Runtime.InteropServices;
using AkerMcp.Server.Platform.Mac;
using AkerMcp.Server.Platform.Windows;

namespace AkerMcp.Server.Platform
{
    /// <summary>
    /// Selects the OS-specific IPlatformInput implementation at runtime.
    /// Returns null on unsupported platforms (currently: Linux).
    /// </summary>
    public static class PlatformInput
    {
        private static readonly Lazy<IPlatformInput?> _current = new Lazy<IPlatformInput?>(Create);

        public static IPlatformInput? Current => _current.Value;

        private static IPlatformInput? Create()
        {
            if (OperatingSystem.IsWindows())
                return new WindowsInputInjector();
            if (OperatingSystem.IsMacOS())
                return new MacInputInjector();
            return null;
        }

        public static string UnsupportedPlatformMessage =>
            $"OS-level input injection is not implemented for this platform " +
            $"({RuntimeInformation.OSDescription}); Windows and macOS are. " +
            "Implement IInputSimulator in your engine adapter to enable send_input into the " +
            "running game; ui_action, which drives the editor's own interface, has no such " +
            "fallback and needs the OS-level path.";
    }
}
