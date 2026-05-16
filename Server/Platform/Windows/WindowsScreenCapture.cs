using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

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
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("Shcore.dll")]
        private static extern int SetProcessDpiAwareness(int value);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

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

            if (!GetClientRect(hWnd, out var rect))
            {
                error = "GetClientRect failed.";
                return null;
            }

            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            if (w <= 0 || h <= 0)
            {
                error = $"Invalid window dimensions: {w}x{h}";
                return null;
            }

            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var gfx = Graphics.FromImage(bmp))
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

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }
}
