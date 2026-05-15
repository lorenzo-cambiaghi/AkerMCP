namespace AkerMcp.Server.Platform
{
    /// <summary>
    /// OS-level screen capture used as fallback when an engine adapter does not
    /// implement IScreenCapture. One implementation per supported OS.
    /// </summary>
    public interface IPlatformScreenCapture
    {
        /// <summary>
        /// Capture the engine's main window. Implementations identify the window
        /// using the engine PID and (on macOS) the window-title prefix supplied
        /// by the engine adapter.
        /// </summary>
        /// <returns>PNG-encoded bytes, or null on failure (window not found, denied permission, etc).</returns>
        byte[]? CaptureMainWindow(int pid, string titlePrefix, out string? error);

        /// <summary>One-time process initialization (e.g. DPI awareness on Windows).</summary>
        void Initialize();
    }
}
