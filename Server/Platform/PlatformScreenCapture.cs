using System;
using System.Runtime.InteropServices;
using AkerMcp.Server.Platform.Mac;
using AkerMcp.Server.Platform.Windows;

namespace AkerMcp.Server.Platform
{
    /// <summary>
    /// Selects the OS-specific IPlatformScreenCapture implementation at runtime.
    /// Returns null on unsupported platforms (currently: Linux).
    /// </summary>
    public static class PlatformScreenCapture
    {
        // Lazy<T> with default ExecutionAndPublication mode: thread-safe; the
        // factory runs at most once and all callers observe the same instance.
        private static readonly Lazy<IPlatformScreenCapture?> _current =
            new Lazy<IPlatformScreenCapture?>(CreateAndInitialize);

        public static IPlatformScreenCapture? Current => _current.Value;

        private static IPlatformScreenCapture? CreateAndInitialize()
        {
            IPlatformScreenCapture? impl = null;
            if (OperatingSystem.IsWindows())
                impl = new WindowsScreenCapture();
            else if (OperatingSystem.IsMacOS())
                impl = new MacScreenCapture();

            impl?.Initialize();
            return impl;
        }

        public static string UnsupportedPlatformMessage =>
            $"OS-level screen capture is not implemented for this platform " +
            $"({RuntimeInformation.OSDescription}). " +
            "Implement IScreenCapture in your engine adapter to enable screenshots.";
    }
}
