using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.Server.Platform.Mac
{
    /// <summary>
    /// macOS OS-level input injection via Quartz Event Services (CGEvent). The counterpart
    /// of the Windows SendInput injector, and it follows the same rules: events go to
    /// whatever is frontmost, so the caller focuses the target window first, and everything
    /// that has to stay together is posted as one uninterrupted sequence.
    ///
    /// Two macOS specifics shape this file. Coordinates are in the global display space
    /// measured in points from the top-left of the main display — not pixels, so a Retina
    /// screen needs no doubling here as long as the capture is taken at nominal resolution
    /// (it is: see MacScreenCapture). And synthetic input is gated by Accessibility
    /// permission: without it CGEventPost silently does nothing at all, which is why
    /// <see cref="Preflight"/> exists and says so in words rather than letting a caller
    /// believe a click happened.
    ///
    /// STATUS: written against the documented Quartz API and compiles on any platform, but
    /// it has not been run on a Mac. Treat the first session on macOS as the real test:
    /// the likely rough edges are the keycode table (it assumes an ANSI layout), the
    /// click-state field for double clicks, and whether the frontmost-window activation
    /// below beats the app that owns the window.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal sealed class MacInputInjector : IPlatformInput
    {
        private readonly HashSet<ushort> _heldKeys = new HashSet<ushort>();
        private readonly HashSet<uint> _heldMouseUp = new HashSet<uint>(); // the UP event type to emit
        private readonly object _lock = new object();

        // Where the pointer was last put. macOS mouse events carry their own position, so a
        // button press has to be told where it happens; there is no "current cursor" the way
        // a Windows mouse event inherits one.
        private CGPoint _cursor;

        public bool Inject(IReadOnlyList<InputEvent> events, out int dispatched, out string? error)
        {
            dispatched = 0;
            if (!Preflight(out error)) return false;

            var skipped = new List<string>();
            foreach (var ev in events)
            {
                switch ((ev.Type ?? "key").ToLowerInvariant())
                {
                    case "key":
                        if (ev.Key == null || !TryMapKey(ev.Key, out var code))
                        {
                            skipped.Add($"key '{ev.Key}'");
                            continue;
                        }
                        if (ev.HoldMs > 0)
                        {
                            PostKey(code, down: true);
                            Sleep(ev.HoldMs);
                            PostKey(code, down: false);
                        }
                        else
                        {
                            PostKey(code, down: ev.Pressed);
                            lock (_lock) { if (ev.Pressed) _heldKeys.Add(code); else _heldKeys.Remove(code); }
                        }
                        dispatched++;
                        break;

                    case "mouse_button":
                        if (!TryMapMouseButton(ev.Button, out var down, out var up, out var button))
                        {
                            skipped.Add($"button '{ev.Button}'");
                            continue;
                        }
                        if (ev.HoldMs > 0)
                        {
                            PostMouse(down, _cursor, button, 1);
                            Sleep(ev.HoldMs);
                            PostMouse(up, _cursor, button, 1);
                        }
                        else
                        {
                            PostMouse(ev.Pressed ? down : up, _cursor, button, 1);
                            lock (_lock) { if (ev.Pressed) _heldMouseUp.Add(up); else _heldMouseUp.Remove(up); }
                        }
                        dispatched++;
                        break;

                    case "mouse_move":
                        _cursor = new CGPoint { x = ev.X, y = ev.Y };
                        PostMouse(kCGEventMouseMoved, _cursor, kCGMouseButtonLeft, 0);
                        dispatched++;
                        break;

                    case "action":
                        skipped.Add($"action '{ev.Action}'");
                        break;

                    default:
                        skipped.Add($"type '{ev.Type}'");
                        break;
                }
            }

            if (dispatched == 0)
            {
                error = skipped.Count > 0
                    ? $"Nothing dispatched; unsupported at OS level: {string.Join(", ", skipped)}."
                    : "No input events to dispatch.";
                return false;
            }

            if (skipped.Count > 0)
                error = $"Skipped (no OS-level equivalent): {string.Join(", ", skipped)}.";
            return true;
        }

        public void ReleaseAll()
        {
            ushort[] keys;
            uint[] mouseUps;
            lock (_lock)
            {
                keys = new ushort[_heldKeys.Count]; _heldKeys.CopyTo(keys); _heldKeys.Clear();
                mouseUps = new uint[_heldMouseUp.Count]; _heldMouseUp.CopyTo(mouseUps); _heldMouseUp.Clear();
            }
            foreach (var k in keys) PostKey(k, down: false);
            foreach (var up in mouseUps) PostMouse(up, _cursor, kCGMouseButtonLeft, 1);
        }

        // ---- UI actions ----------------------------------------------------

        public bool MoveTo(int screenX, int screenY, out string? error)
        {
            if (!Preflight(out error)) return false;
            _cursor = new CGPoint { x = screenX, y = screenY };
            PostMouse(kCGEventMouseMoved, _cursor, kCGMouseButtonLeft, 0);
            return true;
        }

        public bool Click(int screenX, int screenY, string button, int count, out string? error)
        {
            if (!Preflight(out error)) return false;
            if (!TryMapMouseButton(button, out var down, out var up, out var which))
            {
                error = $"Unknown mouse button '{button}'.";
                return false;
            }

            count = Math.Max(1, Math.Min(3, count));
            _cursor = new CGPoint { x = screenX, y = screenY };
            PostMouse(kCGEventMouseMoved, _cursor, kCGMouseButtonLeft, 0);
            Thread.Sleep(HoverSettleMs);

            // A double click on macOS is not two clicks close together: it is one click whose
            // kCGMouseEventClickState says 2. Sending two separate single clicks gives two
            // single clicks, however fast they are.
            for (int i = 0; i < count; i++)
            {
                if (i > 0) Thread.Sleep(MultiClickGapMs);
                PostMouse(down, _cursor, which, i + 1);
                Thread.Sleep(ClickHoldMs);
                PostMouse(up, _cursor, which, i + 1);
            }
            return true;
        }

        public bool Drag(int fromX, int fromY, int toX, int toY, string button, out string? error)
        {
            if (!Preflight(out error)) return false;
            if (!TryMapMouseButton(button, out var down, out var up, out var which))
            {
                error = $"Unknown mouse button '{button}'.";
                return false;
            }
            uint dragged = which == kCGMouseButtonRight ? kCGEventRightMouseDragged : kCGEventLeftMouseDragged;

            _cursor = new CGPoint { x = fromX, y = fromY };
            PostMouse(kCGEventMouseMoved, _cursor, which, 0);
            Thread.Sleep(HoverSettleMs);
            PostMouse(down, _cursor, which, 1);
            lock (_lock) _heldMouseUp.Add(up);
            try
            {
                Thread.Sleep(DragGrabMs);
                const int steps = 12;
                for (int i = 1; i <= steps; i++)
                {
                    _cursor = new CGPoint
                    {
                        x = fromX + (toX - fromX) * i / (double)steps,
                        y = fromY + (toY - fromY) * i / (double)steps,
                    };
                    // While a button is held the motion event is a *drag*, not a move: a
                    // plain move with the button down is ignored by most drag handling.
                    PostMouse(dragged, _cursor, which, 1);
                    Thread.Sleep(DragStepMs);
                }
                Thread.Sleep(DragGrabMs);
            }
            finally
            {
                PostMouse(up, _cursor, which, 1);
                lock (_lock) _heldMouseUp.Remove(up);
            }
            return true;
        }

        public bool Scroll(int screenX, int screenY, int clicks, bool horizontal, out string? error)
        {
            if (!Preflight(out error)) return false;
            if (clicks == 0)
            {
                error = "Scroll amount is zero.";
                return false;
            }

            _cursor = new CGPoint { x = screenX, y = screenY };
            PostMouse(kCGEventMouseMoved, _cursor, kCGMouseButtonLeft, 0);
            Thread.Sleep(HoverSettleMs);

            var perStep = clicks > 0 ? 1 : -1;
            for (int i = 0; i < Math.Abs(clicks); i++)
            {
                // wheelCount 2 so the second axis can carry horizontal scrolling; the unit is
                // lines, which is what a notch of a real wheel reports.
                IntPtr ev = horizontal
                    ? CGEventCreateScrollWheelEvent(IntPtr.Zero, kCGScrollEventUnitLine, 2, 0, perStep)
                    : CGEventCreateScrollWheelEvent(IntPtr.Zero, kCGScrollEventUnitLine, 2, perStep, 0);
                Post(ev);
                Thread.Sleep(ScrollStepMs);
            }
            return true;
        }

        public bool TypeText(string text, out string? error)
        {
            if (!Preflight(out error)) return false;
            if (string.IsNullOrEmpty(text))
            {
                error = "No text to type.";
                return false;
            }

            // One character at a time, carried as a Unicode string on a keycode-less event:
            // the text then arrives the same whatever keyboard layout is active, and
            // characters that no key produces still arrive.
            foreach (var ch in text)
            {
                if (ch == '\r') continue;

                if (ch == '\n') { PostKey(kVK_Return, true); PostKey(kVK_Return, false); }
                else if (ch == '\t') { PostKey(kVK_Tab, true); PostKey(kVK_Tab, false); }
                else
                {
                    PostUnicode(ch, down: true);
                    PostUnicode(ch, down: false);
                }
                Thread.Sleep(TypeGapMs);
            }
            return true;
        }

        public bool KeyChord(string chord, out string? error)
        {
            if (!Preflight(out error)) return false;
            if (string.IsNullOrWhiteSpace(chord))
            {
                error = "No key to send.";
                return false;
            }

            var parts = chord.Split('+');
            ulong flags = 0;
            ushort mainKey = 0;

            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i].Trim();
                if (part.Length == 0) part = "+";

                if (i < parts.Length - 1)
                {
                    if (!TryMapModifierFlag(part, out var flag))
                    {
                        error = $"'{part}' is not a modifier. Write shortcuts as 'cmd+shift+s', " +
                                "with cmd/ctrl/shift/alt first and the key last.";
                        return false;
                    }
                    flags |= flag;
                }
                else if (!TryMapKey(part, out mainKey))
                {
                    error = $"Unknown key '{part}'.";
                    return false;
                }
            }

            // The modifiers ride as flags on the key event itself. Posting them as separate
            // key presses works too, but leaves them stuck down if anything throws in between.
            IntPtr down = CGEventCreateKeyboardEvent(IntPtr.Zero, mainKey, true);
            IntPtr up = CGEventCreateKeyboardEvent(IntPtr.Zero, mainKey, false);
            if (flags != 0)
            {
                CGEventSetFlags(down, flags);
                CGEventSetFlags(up, flags);
            }
            Post(down);
            Thread.Sleep(ClickHoldMs);
            Post(up);
            return true;
        }

        // ---- permission ----------------------------------------------------

        /// <summary>
        /// Synthetic input needs Accessibility permission, and without it CGEventPost fails
        /// silently — the call returns, nothing moves, and a caller that trusts the return
        /// value reports a click that never happened. So it is checked up front and the
        /// answer says exactly which switch to flip.
        /// </summary>
        private static bool Preflight(out string? error)
        {
            error = null;
            try
            {
                if (AXIsProcessTrusted()) return true;
            }
            catch (Exception ex)
            {
                error = $"Cannot query Accessibility permission: {ex.Message}";
                return false;
            }

            error =
                "macOS has not granted this process Accessibility permission, so synthetic input " +
                "would be discarded without an error.\n" +
                "  System Settings -> Privacy & Security -> Accessibility\n" +
                "Add (or enable) the binary running the AkerMcp server (typically 'dotnet'), then " +
                "RESTART the server: macOS caches the denial for the life of the process.";
            return false;
        }

        // Timings, matching the Windows injector so a sequence behaves the same on both.
        private const int HoverSettleMs = 20;
        private const int ClickHoldMs = 25;
        private const int MultiClickGapMs = 60;
        private const int DragGrabMs = 60;
        private const int DragStepMs = 12;
        private const int ScrollStepMs = 25;

        /// <summary>See the Windows injector: how fast a window takes synthetic text is the
        /// window's property, so the pause is generous by default and overridable.</summary>
        private static readonly int TypeGapMs =
            int.TryParse(Environment.GetEnvironmentVariable("AKER_MCP_TYPE_GAP_MS"), out var gap)
            && gap >= 0 && gap <= 500 ? gap : 30;

        private static void Sleep(double ms)
        {
            var clamped = Math.Max(0, Math.Min(5000, ms));
            if (clamped > 0) Thread.Sleep((int)clamped);
        }

        // ---- Quartz plumbing ----

        private static void Post(IntPtr ev)
        {
            if (ev == IntPtr.Zero) return;
            try { CGEventPost(kCGHIDEventTap, ev); }
            finally { CFRelease(ev); }
        }

        private static void PostKey(ushort keycode, bool down)
            => Post(CGEventCreateKeyboardEvent(IntPtr.Zero, keycode, down));

        private static void PostUnicode(char ch, bool down)
        {
            // Keycode 0 with an attached Unicode string: the event carries the character
            // rather than a key position.
            IntPtr ev = CGEventCreateKeyboardEvent(IntPtr.Zero, 0, down);
            if (ev == IntPtr.Zero) return;
            var chars = new[] { ch };
            CGEventKeyboardSetUnicodeString(ev, 1, chars);
            Post(ev);
        }

        private static void PostMouse(uint type, CGPoint at, uint button, int clickState)
        {
            IntPtr ev = CGEventCreateMouseEvent(IntPtr.Zero, type, at, button);
            if (ev == IntPtr.Zero) return;
            if (clickState > 0) CGEventSetIntegerValueField(ev, kCGMouseEventClickState, clickState);
            Post(ev);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CGPoint { public double x, y; }

        private const string CoreGraphics =
            "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        private const string CoreFoundation =
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string ApplicationServices =
            "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

        private const uint kCGHIDEventTap = 0;

        private const uint kCGEventLeftMouseDown = 1;
        private const uint kCGEventLeftMouseUp = 2;
        private const uint kCGEventRightMouseDown = 3;
        private const uint kCGEventRightMouseUp = 4;
        private const uint kCGEventMouseMoved = 5;
        private const uint kCGEventLeftMouseDragged = 6;
        private const uint kCGEventRightMouseDragged = 7;
        private const uint kCGEventOtherMouseDown = 25;
        private const uint kCGEventOtherMouseUp = 26;

        private const uint kCGMouseButtonLeft = 0;
        private const uint kCGMouseButtonRight = 1;
        private const uint kCGMouseButtonCenter = 2;

        private const uint kCGMouseEventClickState = 1;
        private const uint kCGScrollEventUnitLine = 1;

        private const ulong kCGEventFlagMaskShift = 0x00020000;
        private const ulong kCGEventFlagMaskControl = 0x00040000;
        private const ulong kCGEventFlagMaskAlternate = 0x00080000;
        private const ulong kCGEventFlagMaskCommand = 0x00100000;

        [DllImport(CoreGraphics)]
        private static extern IntPtr CGEventCreateMouseEvent(IntPtr source, uint type, CGPoint pos, uint button);

        [DllImport(CoreGraphics)]
        private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort keycode, bool keyDown);

        [DllImport(CoreGraphics)]
        private static extern IntPtr CGEventCreateScrollWheelEvent(
            IntPtr source, uint units, uint wheelCount, int wheel1, int wheel2);

        [DllImport(CoreGraphics)]
        private static extern void CGEventPost(uint tap, IntPtr theEvent);

        [DllImport(CoreGraphics)]
        private static extern void CGEventSetIntegerValueField(IntPtr theEvent, uint field, long value);

        [DllImport(CoreGraphics)]
        private static extern void CGEventSetFlags(IntPtr theEvent, ulong flags);

        [DllImport(CoreGraphics, CharSet = CharSet.Unicode)]
        private static extern void CGEventKeyboardSetUnicodeString(
            IntPtr theEvent, long stringLength, [MarshalAs(UnmanagedType.LPArray)] char[] unicodeString);

        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr cf);

        [DllImport(ApplicationServices)]
        private static extern bool AXIsProcessTrusted();

        private static bool TryMapMouseButton(string? button, out uint down, out uint up, out uint which)
        {
            switch ((button ?? "left").ToLowerInvariant())
            {
                case "left":
                    down = kCGEventLeftMouseDown; up = kCGEventLeftMouseUp; which = kCGMouseButtonLeft; return true;
                case "right":
                    down = kCGEventRightMouseDown; up = kCGEventRightMouseUp; which = kCGMouseButtonRight; return true;
                case "middle":
                    down = kCGEventOtherMouseDown; up = kCGEventOtherMouseUp; which = kCGMouseButtonCenter; return true;
                default:
                    down = up = which = 0; return false;
            }
        }

        private static bool TryMapModifierFlag(string name, out ulong flag)
        {
            switch (name.ToLowerInvariant())
            {
                case "cmd": case "command": case "win": case "meta": case "super":
                    flag = kCGEventFlagMaskCommand; return true;
                case "ctrl": case "control":
                    flag = kCGEventFlagMaskControl; return true;
                case "shift":
                    flag = kCGEventFlagMaskShift; return true;
                case "alt": case "option": case "opt":
                    flag = kCGEventFlagMaskAlternate; return true;
                default:
                    flag = 0; return false;
            }
        }

        // Virtual keycodes are positions on the keyboard, not characters, and the numbers
        // below are the ANSI layout's. Letters and digits therefore assume a US layout; text
        // does not go through here (TypeText carries Unicode instead), so this matters only
        // for shortcuts, which are written in ANSI terms anyway.
        private const ushort kVK_Return = 36;
        private const ushort kVK_Tab = 48;
        private const ushort kVK_Space = 49;
        private const ushort kVK_Delete = 51;      // backspace
        private const ushort kVK_Escape = 53;
        private const ushort kVK_ForwardDelete = 117;

        private static readonly Dictionary<char, ushort> AnsiKeys = new Dictionary<char, ushort>
        {
            ['a'] = 0, ['s'] = 1, ['d'] = 2, ['f'] = 3, ['h'] = 4, ['g'] = 5, ['z'] = 6, ['x'] = 7,
            ['c'] = 8, ['v'] = 9, ['b'] = 11, ['q'] = 12, ['w'] = 13, ['e'] = 14, ['r'] = 15,
            ['y'] = 16, ['t'] = 17, ['1'] = 18, ['2'] = 19, ['3'] = 20, ['4'] = 21, ['6'] = 22,
            ['5'] = 23, ['='] = 24, ['9'] = 25, ['7'] = 26, ['-'] = 27, ['8'] = 28, ['0'] = 29,
            [']'] = 30, ['o'] = 31, ['u'] = 32, ['['] = 33, ['i'] = 34, ['p'] = 35, ['l'] = 37,
            ['j'] = 38, ['\''] = 39, ['k'] = 40, [';'] = 41, ['\\'] = 42, [','] = 43, ['/'] = 44,
            ['n'] = 45, ['m'] = 46, ['.'] = 47, ['`'] = 50,
        };

        private static readonly Dictionary<string, ushort> NamedKeys =
            new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
            {
                ["space"] = kVK_Space, ["spacebar"] = kVK_Space, [" "] = kVK_Space,
                ["enter"] = kVK_Return, ["return"] = kVK_Return,
                ["escape"] = kVK_Escape, ["esc"] = kVK_Escape,
                ["tab"] = kVK_Tab,
                ["backspace"] = kVK_Delete,
                ["delete"] = kVK_ForwardDelete, ["del"] = kVK_ForwardDelete,
                ["home"] = 115, ["end"] = 119, ["pageup"] = 116, ["pgup"] = 116,
                ["pagedown"] = 121, ["pgdn"] = 121, ["insert"] = 114, ["ins"] = 114,
                ["left"] = 123, ["leftarrow"] = 123, ["right"] = 124, ["rightarrow"] = 124,
                ["down"] = 125, ["downarrow"] = 125, ["up"] = 126, ["uparrow"] = 126,
                ["shift"] = 56, ["lshift"] = 56,
                ["ctrl"] = 59, ["control"] = 59, ["lctrl"] = 59,
                ["alt"] = 58, ["option"] = 58, ["lalt"] = 58,
                ["cmd"] = 55, ["command"] = 55, ["win"] = 55, ["meta"] = 55, ["super"] = 55,
                ["comma"] = 43, ["period"] = 47, ["minus"] = 27, ["plus"] = 24, ["equals"] = 24,
                ["slash"] = 44, ["backslash"] = 42, ["semicolon"] = 41, ["quote"] = 39,
                ["leftbracket"] = 33, ["rightbracket"] = 30, ["backquote"] = 50, ["tilde"] = 50,
            };

        // F1-F20 are not consecutive on macOS; this is the documented order.
        private static readonly ushort[] FunctionKeys =
        {
            122, 120, 99, 118, 96, 97, 98, 100, 101, 109, 103, 111,  // F1-F12
            105, 107, 113, 106, 64, 79, 80, 90,                       // F13-F20
        };

        private static bool TryMapKey(string key, out ushort code)
        {
            var k = key.Trim();
            if (k.Length == 0) k = key;
            if (k.Length == 0) { code = 0; return false; }

            if (k.Length == 1 && AnsiKeys.TryGetValue(char.ToLowerInvariant(k[0]), out code))
                return true;

            if (NamedKeys.TryGetValue(k, out code)) return true;

            if ((k[0] == 'f' || k[0] == 'F') && k.Length <= 3
                && int.TryParse(k.Substring(1), out var fn) && fn >= 1 && fn <= FunctionKeys.Length)
            {
                code = FunctionKeys[fn - 1];
                return true;
            }

            code = 0;
            return false;
        }
    }
}
