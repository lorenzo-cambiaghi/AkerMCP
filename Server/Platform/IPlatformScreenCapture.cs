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
    /// Where a window sits on screen, and how big it is. This is what turns a point read off
    /// a screenshot into a point the mouse can be sent to; without it the caller knows what
    /// the window looks like but not where it is.
    ///
    /// The units are whatever the OS moves the pointer in: physical pixels on Windows,
    /// points on macOS, where a Retina window measures half what its backing store does.
    /// They are therefore not necessarily the captured image's pixels, and the ratio between
    /// the two is exactly what a caller converting a coordinate needs.
    /// </summary>
    public sealed class WindowGeometry
    {
        /// <summary>The full title of the window that matched.</summary>
        public string Title { get; set; } = "";
        /// <summary>Origin in screen coordinates (negative on a monitor left of or above the primary).</summary>
        public int OriginX { get; set; }
        public int OriginY { get; set; }
        /// <summary>Size of the same rectangle the capture returns, in the units above.</summary>
        public int Width { get; set; }
        public int Height { get; set; }
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

        /// <summary>
        /// Bring the first visible top-level window whose title contains
        /// <paramref name="titleSubstring"/> (case-insensitive) to the foreground,
        /// restoring it first if it is minimized.
        /// </summary>
        /// <returns>true on success; false (with <paramref name="error"/> set) if no
        /// window matched or the OS refused the foreground change.</returns>
        bool FocusWindowByTitle(string titleSubstring, out string? error);

        /// <summary>
        /// Where the first window matching <paramref name="titleSubstring"/> has its client
        /// area on the virtual screen. Pairs with <see cref="CaptureWindowByTitle"/>: the
        /// rectangle described here is the one the capture returns, so a pixel in that image
        /// maps to the screen by adding this origin. Returns false (with
        /// <paramref name="error"/> set) when no window matches or the OS cannot say.
        /// </summary>
        bool TryGetWindowGeometry(string titleSubstring, out WindowGeometry? geometry, out string? error);

        /// <summary>One-time process initialization (e.g. DPI awareness on Windows).</summary>
        void Initialize();
    }
}
