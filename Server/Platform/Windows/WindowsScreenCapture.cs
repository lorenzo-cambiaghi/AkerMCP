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

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        private const int GWL_STYLE = -16;
        private const long WS_POPUP = 0x80000000L;
        private const long WS_CAPTION = 0x00C00000L;

        // A menu, dropdown or tooltip: WS_POPUP and no caption. A second document window of the
        // same app has a caption, and is a window of its own rather than part of this one.
        private static bool IsFramelessPopup(IntPtr hWnd)
        {
            var style = (long)GetWindowLongPtr(hWnd, GWL_STYLE);
            return (style & WS_POPUP) != 0 && (style & WS_CAPTION) != WS_CAPTION;
        }

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

            // Already in front? Then leave it alone. Raising a window that is already the
            // foreground is not a no-op from the interface's point of view: it closes any
            // menu or popup that window has open, which is exactly the thing a caller is
            // usually in the middle of clicking through. The popup is a window of its own and
            // holds the focus, so the test is "the foreground is a popup of the same process" —
            // not "the foreground is this exact handle".
            //
            // A popup, and not any window of the process: the editor's main window and one of
            // its floating windows (Project Settings, a dialog) share a process too, and taking
            // the main one for the floating one sent a Delete meant for a dialog to the Hierarchy.
            var current = GetForegroundWindow();
            if (current == hWnd) return true;
            if (current != IntPtr.Zero && IsFramelessPopup(current))
            {
                GetWindowThreadProcessId(current, out var currentPid);
                GetWindowThreadProcessId(hWnd, out var targetPid);
                if (currentPid != 0 && currentPid == targetPid) return true;
            }

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

            ComposePopups(hWnd, bmp, origin.X, origin.Y);

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }

        /// <summary>
        /// Draw the window's own menus and popups onto the capture, where they actually are.
        ///
        /// A dropdown is not part of the window that owns it: Windows gives it a window of its
        /// own, floating above. PrintWindow on the parent therefore renders the interface with
        /// the menu missing — the click that opened it appears to have done nothing, and the
        /// entry to click next is invisible. Compositing them back is enough to make menus
        /// usable, and only the image needs it: a click is sent in screen coordinates, so it
        /// already lands on whatever is topmost at that point.
        ///
        /// Only the owning process's frameless popups are taken, so an unrelated window that
        /// happens to overlap is not pasted into the picture. Failures are swallowed: a menu
        /// that cannot be captured is worth less than the screenshot it would otherwise cost.
        /// </summary>
        private static void ComposePopups(IntPtr owner, Bitmap target, int clientOriginX, int clientOriginY)
        {
            try
            {
                GetWindowThreadProcessId(owner, out var ownerPid);
                if (ownerPid == 0) return;

                // Only while that process is in front. The popups themselves are identified by
                // owner, but their PIXELS come off the screen, and the screen shows whatever is
                // topmost at those coordinates. If the window is behind something else, that
                // something else would be pasted into the capture — another application's
                // content, in a picture of this one. A background window has no menu open worth
                // capturing anyway, so the safe case and the useful case are the same case.
                var foreground = GetForegroundWindow();
                if (foreground == IntPtr.Zero) return;
                GetWindowThreadProcessId(foreground, out var foregroundPid);
                if (foregroundPid != ownerPid) return;

                var popups = new List<IntPtr>();
                EnumWindows((hWnd, _) =>
                {
                    if (hWnd == owner || !IsWindowVisible(hWnd)) return true;
                    GetWindowThreadProcessId(hWnd, out var pid);
                    if (pid != ownerPid) return true;

                    if (!IsFramelessPopup(hWnd)) return true;

                    popups.Add(hWnd);
                    return true;
                }, IntPtr.Zero);

                if (popups.Count == 0) return;

                // Bottom-up: EnumWindows walks front to back, so painting in reverse leaves the
                // topmost popup on top, as the screen has it.
                popups.Reverse();

                using var gfx = Graphics.FromImage(target);
                foreach (var popup in popups)
                {
                    if (!GetWindowRect(popup, out var r)) continue;
                    int w = r.Right - r.Left, h = r.Bottom - r.Top;
                    if (w <= 0 || h <= 0 || (long)w * h > MaxPopupPixels) continue;

                    int x = r.Left - clientOriginX, y = r.Top - clientOriginY;
                    if (x + w <= 0 || y + h <= 0 || x >= target.Width || y >= target.Height) continue;

                    using var shot = CapturePopupPixels(popup, r);
                    if (shot == null) continue;
                    gfx.DrawImageUnscaled(shot, x, y);
                }
            }
            catch { /* the window beneath is still worth returning */ }
        }

        /// <summary>
        /// A guard against pasting something absurd if a popup reports a silly size. Four
        /// million pixels is roughly 2000x2000 — enormous for a menu, and still only 16 MB of
        /// bitmap. The previous 8000x8000 would have allowed a quarter of a gigabyte to be
        /// allocated on the strength of a number reported by a window.
        /// </summary>
        private const long MaxPopupPixels = 4_000_000L;

        /// <summary>
        /// A popup's pixels, read off the screen rather than asked of the window.
        ///
        /// PrintWindow is the right call for the main window — it works on windows that are
        /// covered or behind others. It is the wrong call here: a modern toolkit's dropdown is
        /// a layered, composited window, and PrintWindow hands back an empty rectangle for it
        /// (Avalonia's menus come out blank). A popup is by definition on top and visible, so
        /// copying the screen where it sits gets the real thing. Returns null rather than a
        /// blank, so a failure leaves the interface underneath visible instead of a white hole.
        /// </summary>
        private static Bitmap? CapturePopupPixels(IntPtr popup, RECT r)
        {
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            IntPtr screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return null;

            Bitmap? shot = null;
            try
            {
                shot = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(shot);
                IntPtr dst = g.GetHdc();
                try
                {
                    if (!BitBlt(dst, 0, 0, w, h, screenDc, r.Left, r.Top, SRCCOPY))
                    {
                        g.ReleaseHdc(dst);
                        shot.Dispose();
                        return null;
                    }
                }
                finally
                {
                    try { g.ReleaseHdc(dst); } catch { /* released above on the failure path */ }
                }
                return shot;
            }
            catch
            {
                shot?.Dispose();
                return null;
            }
            finally { ReleaseDC(IntPtr.Zero, screenDc); }
        }

        private const int SRCCOPY = 0x00CC0020;

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h,
                                          IntPtr hdcSrc, int srcX, int srcY, int rop);
    }
}
