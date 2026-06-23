using System.Collections.Generic;

namespace AkerMcp.Server.Platform
{
    /// <summary>A visible top-level OS window.</summary>
    public sealed class WindowSummary
    {
        public string Title { get; set; } = "";
        public int Pid { get; set; }
        public string ProcessName { get; set; } = "";
    }

    /// <summary>
    /// OS-level screen capture. Used both as a fallback when an engine adapter does
    /// not implement IScreenCapture, and directly by the list_windows/capture_window
    /// tools to capture any window on the machine. One implementation per OS.
    /// </summary>
    public interface IPlatformScreenCapture
    {
        /// <summary>Visible top-level windows that have a non-empty title.</summary>
        IReadOnlyList<WindowSummary> ListWindows();

        /// <summary>
        /// Capture the first visible top-level window whose title contains
        /// <paramref name="titleSubstring"/> (case-insensitive).
        /// </summary>
        /// <returns>PNG-encoded bytes, or null on failure (no match, denied, etc).</returns>
        byte[]? CaptureWindowByTitle(string titleSubstring, out string? error);

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
