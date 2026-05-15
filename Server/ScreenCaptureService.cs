using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AkerMcp.Server
{
    /// <summary>
    /// OS-level window capture via PrintWindow. Captures occluded windows without
    /// stealing foreground. Windows-only fallback used when the engine adapter does
    /// not implement IScreenCapture.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class ScreenCaptureService
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

        public static void EnsureDpiAwareness()
        {
            try { SetProcessDpiAwareness(2); /* PROCESS_PER_MONITOR_DPI_AWARE */ }
            catch { /* already set or unsupported, ignore */ }
        }

        /// <returns>Raw PNG-encoded bytes of the captured window, or null on failure.</returns>
        public static byte[]? CaptureWindow(long windowHandle)
        {
            var hWnd = new IntPtr(windowHandle);
            if (!IsWindow(hWnd)) return null;
            if (!GetClientRect(hWnd, out var rect)) return null;

            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            if (w <= 0 || h <= 0) return null;

            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var gfx = Graphics.FromImage(bmp))
            {
                IntPtr hdc = gfx.GetHdc();
                try
                {
                    if (!PrintWindow(hWnd, hdc, PW_RENDERFULLCONTENT))
                        return null;
                }
                finally { gfx.ReleaseHdc(hdc); }
            }

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }
}
