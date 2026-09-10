using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AkerMcp.Server.Platform.Windows
{
    /// <summary>
    /// Windows OS-level window capture via PrintWindow. Captures occluded windows
    /// without stealing foreground focus.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class WindowsScreenCapture : IPlatformScreenCapture
    {
        private const uint PW_RENDERFULLCONTENT = 0x00000002;

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("Shcore.dll")]
        private static extern int SetProcessDpiAwareness(int value);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        // Window-focus interop (used by FocusWindowByTitle).
        private const int SW_RESTORE = 9;
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        public void Initialize()
        {
            try { SetProcessDpiAwareness(2); /* PROCESS_PER_MONITOR_DPI_AWARE */ }
            catch { /* already set or unsupported, ignore */ }
        }

        public byte[]? CaptureMainWindow(int pid, string titlePrefix, out string? error)
        {
            error = null;

            IntPtr hWnd;
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Refresh();
                hWnd = process.MainWindowHandle;
            }
            catch (Exception ex)
            {
                error = $"Cannot resolve process PID {pid}: {ex.Message}";
                return null;
            }

            if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
            {
                error = $"Process {pid} has no main window handle (engine may be minimized or headless).";
                return null;
            }

            return CaptureHwnd(hWnd, out error);
        }

        public IReadOnlyList<WindowSummary> ListWindows()
        {
            var result = new List<WindowSummary>();
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                int len = GetWindowTextLength(hWnd);
                if (len <= 0) return true;

                var sb = new StringBuilder(len + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                var title = sb.ToString();
                if (string.IsNullOrWhiteSpace(title)) return true;

                GetWindowThreadProcessId(hWnd, out var pid);
                string procName = "";
                try { using var p = Process.GetProcessById((int)pid); procName = p.ProcessName; }
                catch { /* process may have exited */ }

                result.Add(new WindowSummary { Title = title, Pid = (int)pid, ProcessName = procName });
                return true;
            }, IntPtr.Zero);
            return result;
        }

        public byte[]? CaptureWindowByTitle(string titleSubstring, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(titleSubstring))
            {
                error = "A non-empty 'title' substring is required.";
                return null;
            }

            IntPtr match = IntPtr.Zero;
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                int len = GetWindowTextLength(hWnd);
                if (len <= 0) return true;

                var sb = new StringBuilder(len + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                if (sb.ToString().IndexOf(titleSubstring, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    match = hWnd;
                    return false; // stop enumeration at first match
                }
                return true;
            }, IntPtr.Zero);

            if (match == IntPtr.Zero)
            {
                error = $"No visible window with a title containing '{titleSubstring}' was found.";
                return null;
            }
            return CaptureHwnd(match, out error);
        }

        public bool FocusWindowByTitle(string titleSubstring, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(titleSubstring))
            {
                error = "A non-empty 'title' substring is required.";
                return false;
            }

            var hWnd = FindWindowByTitle(titleSubstring);
            if (hWnd == IntPtr.Zero)
            {
                error = $"No visible window with a title containing '{titleSubstring}' was found.";
                return false;
            }

            if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);

            // Work around Windows' foreground lock by briefly attaching our input queue
            // to the current foreground window's thread.
            var foreground = GetForegroundWindow();
            uint foreThread = GetWindowThreadProcessId(foreground, out _);
            uint thisThread = GetCurrentThreadId();
            bool attached = foreThread != 0 && foreThread != thisThread
                            && AttachThreadInput(foreThread, thisThread, true);
            try
            {
                BringWindowToTop(hWnd);
                if (!SetForegroundWindow(hWnd) && GetForegroundWindow() != hWnd)
                {
                    error = "The OS refused to change the foreground window (foreground lock).";
                    return false;
                }
                return true;
            }
            finally
            {
                if (attached) AttachThreadInput(foreThread, thisThread, false);
            }
        }

        public bool TryGetWindowGeometry(string titleSubstring, out WindowGeometry? geometry, out string? error)
        {
            geometry = null;
            error = null;
            if (string.IsNullOrWhiteSpace(titleSubstring))
            {
                error = "A non-empty 'title' substring is required.";
                return false;
            }

            var hWnd = FindWindowByTitle(titleSubstring);
            if (hWnd == IntPtr.Zero)
            {
                error = $"No visible window with a title containing '{titleSubstring}' was found.";
                return false;
            }

            if (!GetClientRect(hWnd, out var rect))
            {
                error = "GetClientRect failed.";
                return false;
            }

            // The client rect is window-relative and always starts at 0,0; ClientToScreen turns
            // that corner into the screen coordinate the mouse understands. The process is
            // per-monitor DPI aware (see Initialize), so both are real pixels on the virtual
            // screen and no scaling factor has to be applied on top.
            var origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hWnd, ref origin))
            {
                error = "ClientToScreen failed.";
                return false;
            }

            geometry = new WindowGeometry
            {
                Title = GetTitle(hWnd),
                OriginX = origin.X,
                OriginY = origin.Y,
                Width = rect.Right - rect.Left,
                Height = rect.Bottom - rect.Top,
            };
            return true;
        }

        private static string GetTitle(IntPtr hWnd)
        {
            int len = GetWindowTextLength(hWnd);
            if (len <= 0) return "";
            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static IntPtr FindWindowByTitle(string titleSubstring)
        {
            IntPtr match = IntPtr.Zero;
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                int len = GetWindowTextLength(hWnd);
                if (len <= 0) return true;

                var sb = new StringBuilder(len + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                if (sb.ToString().IndexOf(titleSubstring, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    match = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return match;
        }

        /// <summary>
        /// Capture a window's CLIENT AREA — the part a user points at, without the title bar
        /// and the invisible resize border Windows draws around it.
        ///
        /// The two-step matters, and getting it wrong is silent. PrintWindow always renders the
        /// WHOLE window, starting at the window rect's top-left; asking it to draw into a
        /// client-sized bitmap does not crop anything, it just cuts the bottom-right off. The
        /// picture then shows the title bar at its top while the caller believes the top row is
        /// the first row of the client area, and every coordinate read off it is low by the
        /// title bar's height. Measured on a real editor: 31 px down and 8 px right, enough to
        /// hit the checkbox below the field being aimed at, with nothing in the result to say
        /// so. So: render the window at full size, then cut the client rectangle out of it.
        /// </summary>
        private static byte[]? CaptureHwnd(IntPtr hWnd, out string? error)
        {
            error = null;

            if (!GetClientRect(hWnd, out var client) || !GetWindowRect(hWnd, out var window))
            {
                error = "GetClientRect/GetWindowRect failed.";
                return null;
            }

            int clientW = client.Right - client.Left;
            int clientH = client.Bottom - client.Top;
            if (clientW <= 0 || clientH <= 0)
            {
                error = $"Invalid window dimensions: {clientW}x{clientH}";
                return null;
            }

            int windowW = Math.Max(clientW, window.Right - window.Left);
            int windowH = Math.Max(clientH, window.Bottom - window.Top);

            // Where the client area starts inside that full-window render.
            var origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hWnd, ref origin))
            {
                error = "ClientToScreen failed.";
                return null;
            }
            int offsetX = origin.X - window.Left;
            int offsetY = origin.Y - window.Top;

            using var full = new Bitmap(windowW, windowH, PixelFormat.Format32bppArgb);
            using (var gfx = Graphics.FromImage(full))
            {
                IntPtr hdc = gfx.GetHdc();
                try
                {
                    if (!PrintWindow(hWnd, hdc, PW_RENDERFULLCONTENT))
                    {
                        error = "PrintWindow returned false (window may be DWM-incompatible).";
                        return null;
                    }
                }
                finally { gfx.ReleaseHdc(hdc); }
            }

            // Clamp, so a window whose frame metrics disagree with its bitmap still yields an
            // image rather than an exception.
            offsetX = Math.Max(0, Math.Min(offsetX, windowW - 1));
            offsetY = Math.Max(0, Math.Min(offsetY, windowH - 1));
            int cropW = Math.Min(clientW, windowW - offsetX);
            int cropH = Math.Min(clientH, windowH - offsetY);

            using var bmp = full.Clone(new Rectangle(offsetX, offsetY, cropW, cropH), full.PixelFormat);

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }
}
