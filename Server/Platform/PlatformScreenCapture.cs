using System;

namespace AkerMcp.Server.Platform
{
    /// <summary>
    /// Selects the OS-specific IPlatformScreenCapture implementation at runtime.
    /// Returns null on unsupported platforms (currently: Linux).
    /// </summary>
    public static class PlatformScreenCapture
    {
        private static IPlatformScreenCapture? _current;
        private static bool _initialized;

        public static IPlatformScreenCapture? Current
        {
            get
            {
                if (_initialized) return _current;
                _initialized = true;

                if (OperatingSystem.IsWindows())
                    _current = new WindowsScreenCapture();
                else if (OperatingSystem.IsMacOS())
                    _current = new MacScreenCapture();
                else
                    _current = null;

                _current?.Initialize();
                return _current;
            }
        }

        public static string UnsupportedPlatformMessage =>
            "OS-level screen capture is not implemented for this platform. " +
            "Implement IScreenCapture in your engine adapter to enable screenshots.";
    }
}
