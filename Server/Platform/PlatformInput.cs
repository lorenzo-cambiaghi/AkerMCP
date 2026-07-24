using System;
using System.Runtime.InteropServices;
using AkerMcp.Server.Platform.Windows;

namespace AkerMcp.Server.Platform
{
    /// <summary>
    /// Selects the OS-specific IPlatformInput implementation at runtime.
    /// Returns null on unsupported platforms (currently: macOS and Linux).
    /// </summary>
    public static class PlatformInput
    {
        private static readonly Lazy<IPlatformInput?> _current = new Lazy<IPlatformInput?>(Create);

        public static IPlatformInput? Current => _current.Value;

        private static IPlatformInput? Create()
        {
            if (OperatingSystem.IsWindows())
                return new WindowsInputInjector();
            return null;
        }

        public static string UnsupportedPlatformMessage =>
            $"OS-level input injection is not implemented for this platform " +
            $"({RuntimeInformation.OSDescription}). " +
            "Implement IInputSimulator in your engine adapter to enable send_input.";
    }
}
