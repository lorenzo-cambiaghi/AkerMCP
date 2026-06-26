using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace AkerMcp.Server.Platform.Mac
{
    /// <summary>
    /// macOS OS-level window capture via Quartz (CoreGraphics + ImageIO).
    ///
    /// Window discovery: enumerates on-screen windows owned by the engine PID.
    /// Among those, prefers any whose title contains the engine name (anywhere
    /// in the title — covers both "Unity ..." and "... Godot Engine" formats).
    /// Within the preferred set (or the whole set if no title matches), picks
    /// the window with the largest bounds area as the editor's main window.
    ///
    /// Permission: requires "Screen Recording" in System Settings → Privacy &amp;
    /// Security. Without it, CGWindowListCreateImage returns NULL and we surface
    /// an actionable error.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal sealed class MacScreenCapture : IPlatformScreenCapture
    {
        public void Initialize() { /* no-op on macOS */ }

        // Generic window listing/by-title capture is not yet implemented on macOS
        // (the engine-window fallback below covers the main use case). Graceful stubs
        // so the list_windows/capture_window tools report cleanly rather than crash.
        public IReadOnlyList<WindowSummary> ListWindows() => new List<WindowSummary>();

        public bool FocusWindowByTitle(string titleSubstring, out string? error)
        {
            error = "Focusing windows is not yet implemented on macOS.";
            return false;
        }

        public byte[]? CaptureWindowByTitle(string titleSubstring, out string? error)
        {
            error = "capture_window by title is not yet implemented on macOS.";
            return null;
        }

        public byte[]? CaptureMainWindow(int pid, string titlePrefix, out string? error)
        {
            error = null;

            uint windowID;
            try
            {
                windowID = FindEngineWindowID(pid, titlePrefix, out var findError);
                if (windowID == 0)
                {
                    error = findError ?? $"No on-screen window found for PID {pid} matching prefix '{titlePrefix}'.";
                    return null;
                }
            }
            catch (Exception ex)
            {
                error = $"Window enumeration failed: {ex.Message}";
                return null;
            }

            // NominalResolution captures at the window's logical size (1x), not the
            // native pixel size. On Retina (2x/3x) BestResolution would capture 4-9x
            // more pixels than our 1920px output target, then ImageSharp would have
            // to downscale them — pure waste.
            using var image = new CGImageHandle(CGWindowListCreateImage(
                CGRect.Null,
                kCGWindowListOptionIncludingWindow,
                windowID,
                kCGWindowImageBoundsIgnoreFraming | kCGWindowImageNominalResolution));

            if (image.IsInvalid)
            {
                error =
                    "macOS denied the screen capture (CGWindowListCreateImage returned NULL).\n" +
                    "Grant Screen Recording permission to the process running this tool:\n" +
                    "  System Settings → Privacy & Security → Screen Recording\n" +
                    "Add (or enable) the binary running the AkerMcp server (typically 'dotnet'),\n" +
                    "then RESTART the server — macOS caches the denial until the process restarts.";
                return null;
            }

            return EncodePng(image.DangerousGetHandle(), out error);
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Window discovery
        // ─────────────────────────────────────────────────────────────────────────

        private static uint FindEngineWindowID(int pid, string titlePrefix, out string? error)
        {
            error = null;

            using var windowList = new CFArrayHandle(CGWindowListCopyWindowInfo(
                kCGWindowListOptionOnScreenOnly | kCGWindowListExcludeDesktopElements,
                kCGNullWindowID));

            if (windowList.IsInvalid)
            {
                error = "CGWindowListCopyWindowInfo returned NULL.";
                return 0;
            }

            long count = CFArrayGetCount(windowList.DangerousGetHandle());
            uint bestID = 0;
            double bestArea = -1;
            bool prefixMatched = false;

            for (long i = 0; i < count; i++)
            {
                IntPtr dict = CFArrayGetValueAtIndex(windowList.DangerousGetHandle(), i);
                if (dict == IntPtr.Zero) continue;

                if (!TryGetIntFromDict(dict, "kCGWindowOwnerPID", out var ownerPid) || ownerPid != pid)
                    continue;
                if (!TryGetIntFromDict(dict, "kCGWindowLayer", out var layer) || layer != 0)
                    continue; // skip menu bar, dock, status windows

                if (!TryGetIntFromDict(dict, "kCGWindowNumber", out var windowNumber))
                    continue;

                var bounds = TryGetWindowBounds(dict);
                if (bounds.width <= 1 || bounds.height <= 1) continue;
                double area = bounds.width * bounds.height;

                var title = TryGetStringFromDict(dict, "kCGWindowName") ?? "";
                // Contains (not StartsWith): editor titles vary in format. Unity
                // starts with "Unity 6000…", but Godot ends with "… Godot Engine".
                // Since we've already filtered by PID, false positives are limited
                // to other windows owned by the same engine process — all of which
                // are valid capture targets anyway.
                bool matchesTitle = !string.IsNullOrEmpty(titlePrefix) &&
                                    title.IndexOf(titlePrefix, StringComparison.OrdinalIgnoreCase) >= 0;

                // Prefer title-matched windows. Within the matched (or unmatched) set,
                // pick the largest by area. Promote to "matched mode" the first time
                // we hit a title match, discarding any prior "unmatched best".
                if (matchesTitle && !prefixMatched)
                {
                    prefixMatched = true;
                    bestArea = area;
                    bestID = (uint)windowNumber;
                }
                else if (matchesTitle == prefixMatched && area > bestArea)
                {
                    bestArea = area;
                    bestID = (uint)windowNumber;
                }
            }

            return bestID;
        }

        private static (double width, double height) TryGetWindowBounds(IntPtr dict)
        {
            IntPtr boundsKey = CreateCFString("kCGWindowBounds");
            try
            {
                IntPtr boundsDict = CFDictionaryGetValue(dict, boundsKey);
                if (boundsDict == IntPtr.Zero) return (0, 0);

                double width = ReadDoubleFromBoundsDict(boundsDict, "Width");
                double height = ReadDoubleFromBoundsDict(boundsDict, "Height");
                return (width, height);
            }
            finally { CFRelease(boundsKey); }
        }

        private static double ReadDoubleFromBoundsDict(IntPtr boundsDict, string key)
        {
            IntPtr cfKey = CreateCFString(key);
            try
            {
                IntPtr value = CFDictionaryGetValue(boundsDict, cfKey);
                if (value == IntPtr.Zero) return 0;
                CFNumberGetValue(value, CFNumberType.Float64Type, out double result);
                return result;
            }
            finally { CFRelease(cfKey); }
        }

        private static bool TryGetIntFromDict(IntPtr dict, string key, out int value)
        {
            value = 0;
            IntPtr cfKey = CreateCFString(key);
            try
            {
                IntPtr cfValue = CFDictionaryGetValue(dict, cfKey);
                if (cfValue == IntPtr.Zero) return false;
                return CFNumberGetValue(cfValue, CFNumberType.Int32Type, out value);
            }
            finally { CFRelease(cfKey); }
        }

        private static string? TryGetStringFromDict(IntPtr dict, string key)
        {
            IntPtr cfKey = CreateCFString(key);
            try
            {
                IntPtr cfValue = CFDictionaryGetValue(dict, cfKey);
                if (cfValue == IntPtr.Zero) return null;
                return CFStringToManaged(cfValue);
            }
            finally { CFRelease(cfKey); }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  PNG encoding via ImageIO
        // ─────────────────────────────────────────────────────────────────────────

        private static byte[]? EncodePng(IntPtr cgImage, out string? error)
        {
            error = null;
            using var data = new CFMutableDataHandle(CFDataCreateMutable(IntPtr.Zero, 0));
            if (data.IsInvalid)
            {
                error = "CFDataCreateMutable failed.";
                return null;
            }

            IntPtr utiKey = CreateCFString("public.png");
            try
            {
                using var dest = new CGImageDestinationHandle(
                    CGImageDestinationCreateWithData(data.DangerousGetHandle(), utiKey, 1, IntPtr.Zero));
                if (dest.IsInvalid)
                {
                    error = "CGImageDestinationCreateWithData failed (PNG codec missing?).";
                    return null;
                }

                CGImageDestinationAddImage(dest.DangerousGetHandle(), cgImage, IntPtr.Zero);
                if (!CGImageDestinationFinalize(dest.DangerousGetHandle()))
                {
                    error = "CGImageDestinationFinalize failed.";
                    return null;
                }
            }
            finally { CFRelease(utiKey); }

            long length = CFDataGetLength(data.DangerousGetHandle());
            if (length <= 0) { error = "Encoded PNG is empty."; return null; }

            IntPtr bytePtr = CFDataGetBytePtr(data.DangerousGetHandle());
            var managed = new byte[length];
            Marshal.Copy(bytePtr, managed, 0, (int)length);
            return managed;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  CoreFoundation string helpers
        // ─────────────────────────────────────────────────────────────────────────

        private static IntPtr CreateCFString(string s)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(s);
            return CFStringCreateWithBytes(IntPtr.Zero, bytes, bytes.Length, CFStringEncoding.UTF8, false);
        }

        private static string? CFStringToManaged(IntPtr cfString)
        {
            if (cfString == IntPtr.Zero) return null;
            long length = CFStringGetLength(cfString);
            if (length == 0) return string.Empty;

            // Worst-case UTF-8 byte length for a UTF-16 length is 4 bytes per code unit
            // plus 1 for the terminating NUL.
            long maxBytes = (length * 4) + 1;
            var buffer = new byte[maxBytes];
            if (!CFStringGetCString(cfString, buffer, maxBytes, CFStringEncoding.UTF8))
                return null;
            // Find NUL
            int nul = Array.IndexOf<byte>(buffer, 0);
            int realLen = nul >= 0 ? nul : buffer.Length;
            return System.Text.Encoding.UTF8.GetString(buffer, 0, realLen);
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  P/Invoke surface
        // ─────────────────────────────────────────────────────────────────────────

        private const string CoreGraphics =
            "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        private const string CoreFoundation =
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string ImageIO =
            "/System/Library/Frameworks/ImageIO.framework/ImageIO";

        // CGWindowList option flags (from CGWindow.h)
        private const uint kCGWindowListOptionAll              = 0;
        private const uint kCGWindowListOptionOnScreenOnly     = 1 << 0;
        private const uint kCGWindowListOptionIncludingWindow  = 1 << 3;
        private const uint kCGWindowListExcludeDesktopElements = 1 << 4;
        private const uint kCGNullWindowID = 0;

        // CGWindowImageOption flags
        private const uint kCGWindowImageBoundsIgnoreFraming = 1 << 0;
        private const uint kCGWindowImageBestResolution      = 1 << 3;
        private const uint kCGWindowImageNominalResolution   = 1 << 4;

        // CGRect must be passed BY VALUE to CGWindowListCreateImage (4 doubles
        // = 32 bytes, in XMM0-3 on x86_64 / d0-d3 on ARM64). Passing IntPtr.Zero
        // would corrupt the register layout and the function silently fails.
        //
        // CGRectNull canonical value is { Infinity, Infinity, 0, 0 } per Apple's
        // CGGeometry.h and Mono/Xamarin's convention. With the IncludingWindow
        // listOption the value is ignored, but matching the convention avoids
        // surprises if we ever change listOption.
        [StructLayout(LayoutKind.Sequential)]
        private struct CGRect
        {
            public double X, Y, Width, Height;
            public static CGRect Null => new CGRect
            {
                X = double.PositiveInfinity,
                Y = double.PositiveInfinity,
                Width = 0,
                Height = 0,
            };
        }

        private enum CFStringEncoding : uint
        {
            UTF8 = 0x08000100,
        }

        private enum CFNumberType : long
        {
            Int32Type = 3,
            Float64Type = 6,
        }

        // CoreGraphics
        [DllImport(CoreGraphics)]
        private static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);

        [DllImport(CoreGraphics, EntryPoint = "CGWindowListCreateImage")]
        private static extern IntPtr CGWindowListCreateImage(
            CGRect screenBounds, uint listOption, uint windowID, uint imageOption);

        [DllImport(CoreGraphics)]
        private static extern void CGImageRelease(IntPtr image);

        // CoreFoundation
        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr cf);

        [DllImport(CoreFoundation)]
        private static extern long CFArrayGetCount(IntPtr theArray);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFArrayGetValueAtIndex(IntPtr theArray, long idx);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFDictionaryGetValue(IntPtr theDict, IntPtr key);

        // Boolean returns from CoreFoundation are 1-byte (unsigned char), NOT the
        // 4-byte Win32 BOOL the .NET marshaller defaults to. I1 = 1 byte signed.
        [DllImport(CoreFoundation)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CFNumberGetValue(IntPtr number, CFNumberType type, out int valuePtr);

        [DllImport(CoreFoundation)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CFNumberGetValue(IntPtr number, CFNumberType type, out double valuePtr);

        [DllImport(CoreFoundation)]
        private static extern long CFStringGetLength(IntPtr theString);

        [DllImport(CoreFoundation)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CFStringGetCString(IntPtr theString, byte[] buffer, long bufferSize, CFStringEncoding encoding);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFStringCreateWithBytes(
            IntPtr alloc, byte[] bytes, long numBytes, CFStringEncoding encoding,
            [MarshalAs(UnmanagedType.I1)] bool isExternalRepresentation);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFDataCreateMutable(IntPtr allocator, long capacity);

        [DllImport(CoreFoundation)]
        private static extern long CFDataGetLength(IntPtr theData);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFDataGetBytePtr(IntPtr theData);

        // ImageIO
        [DllImport(ImageIO)]
        private static extern IntPtr CGImageDestinationCreateWithData(
            IntPtr data, IntPtr type, long count, IntPtr options);

        [DllImport(ImageIO)]
        private static extern void CGImageDestinationAddImage(IntPtr idst, IntPtr image, IntPtr properties);

        [DllImport(ImageIO)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CGImageDestinationFinalize(IntPtr idst);

        // ─────────────────────────────────────────────────────────────────────────
        //  SafeHandles (auto-release CF / CG resources)
        // ─────────────────────────────────────────────────────────────────────────

        private sealed class CFArrayHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public CFArrayHandle(IntPtr h) : base(true) { SetHandle(h); }
            protected override bool ReleaseHandle() { CFRelease(handle); return true; }
        }

        private sealed class CFMutableDataHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public CFMutableDataHandle(IntPtr h) : base(true) { SetHandle(h); }
            protected override bool ReleaseHandle() { CFRelease(handle); return true; }
        }

        private sealed class CGImageHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public CGImageHandle(IntPtr h) : base(true) { SetHandle(h); }
            protected override bool ReleaseHandle() { CGImageRelease(handle); return true; }
        }

        private sealed class CGImageDestinationHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public CGImageDestinationHandle(IntPtr h) : base(true) { SetHandle(h); }
            protected override bool ReleaseHandle() { CFRelease(handle); return true; }
        }
    }
}
