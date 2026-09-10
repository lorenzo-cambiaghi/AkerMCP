using System;
using System.Runtime.InteropServices;

namespace AkerMcp.UiProbe
{
    /// <summary>
    /// Where the mouse pointer actually is, asked of the operating system.
    ///
    /// This is the whole reason the bench exists. A tool that drives a screen can always be
    /// checked against itself — convert a coordinate, convert it back, watch the two agree —
    /// and that check passes while every click lands somewhere else, because both halves share
    /// the same wrong assumption. It happened: the coordinate maths was exact to the pixel and
    /// the picture it referred to started 31 px higher than anyone thought. Asking the OS where
    /// the pointer ended up is the one question whose answer does not come from the code under
    /// test.
    ///
    /// Both platforms report in the same units the server sends: physical pixels on Windows,
    /// points on macOS, top-left origin in each case.
    /// </summary>
    public static class Pointer
    {
        public static bool TryGetPosition(out int x, out int y, out string? error)
        {
            x = y = 0;
            error = null;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if (!Windows.GetCursorPos(out var p))
                    {
                        error = "GetCursorPos failed.";
                        return false;
                    }
                    x = p.X; y = p.Y;
                    return true;
                }

                if (OperatingSystem.IsMacOS())
                {
                    // An event with no source carries the current pointer location.
                    IntPtr ev = Mac.CGEventCreate(IntPtr.Zero);
                    if (ev == IntPtr.Zero)
                    {
                        error = "CGEventCreate returned NULL.";
                        return false;
                    }
                    try
                    {
                        var p = Mac.CGEventGetLocation(ev);
                        x = (int)Math.Round(p.X);
                        y = (int)Math.Round(p.Y);
                        return true;
                    }
                    finally { Mac.CFRelease(ev); }
                }

                error = $"Reading the pointer is not implemented on {RuntimeInformation.OSDescription}.";
                return false;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Make this process see real pixels rather than the scaled coordinates Windows hands
        /// to a DPI-unaware program, so a bench on a scaled display measures what the server
        /// measures. No-op elsewhere.
        /// </summary>
        public static void MatchServerCoordinateSpace()
        {
            if (!OperatingSystem.IsWindows()) return;
            try { Windows.SetProcessDPIAware(); } catch { /* older Windows, or already set */ }
        }

        private static class Windows
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct POINT { public int X, Y; }

            [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
            [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        }

        private static class Mac
        {
            private const string CoreGraphics =
                "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
            private const string CoreFoundation =
                "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

            [StructLayout(LayoutKind.Sequential)]
            public struct CGPoint { public double X, Y; }

            [DllImport(CoreGraphics)] public static extern IntPtr CGEventCreate(IntPtr source);
            [DllImport(CoreGraphics)] public static extern CGPoint CGEventGetLocation(IntPtr theEvent);
            [DllImport(CoreFoundation)] public static extern void CFRelease(IntPtr cf);
        }
    }
}
