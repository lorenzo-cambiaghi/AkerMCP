using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.Server.Platform.Windows
{
    /// <summary>
    /// Windows OS-level input injection via user32 SendInput. Events go to whatever
    /// window currently has the foreground focus, so the caller (ToolRegistry.send_input
    /// and ui_action) focuses the target game/engine window first. Canonical key names
    /// (Space, Enter, arrows, letters, digits, function keys, punctuation, modifiers) map
    /// to virtual-key codes; keys are sent with their scancode + the extended-key flag so
    /// arrow/edit keys reach games that read physical scancodes / raw input, not just
    /// message-queue (VK) consumers.
    ///
    /// Absolute coordinates are virtual-screen coordinates: the pointer is placed with
    /// MOUSEEVENTF_VIRTUALDESK against the virtual desktop's own origin and size, so a
    /// window on a second monitor — or on one left of or above the primary, where the
    /// coordinates are negative — is reachable. Normalizing against the primary monitor
    /// instead silently folds every one of those points back onto screen one.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class WindowsInputInjector : IPlatformInput
    {
        // Keys/buttons pressed without a matching release (unbalanced pressed:true), so
        // ReleaseAll() can clear them on exit_play. Guarded by _lock (Inject may run on a
        // pool thread; ReleaseAll from the exit_play handler thread).
        private readonly HashSet<ushort> _heldVks = new HashSet<ushort>();
        private readonly HashSet<uint> _heldMouseUp = new HashSet<uint>(); // the UP flag to emit
        private readonly object _lock = new object();

        public bool Inject(IReadOnlyList<InputEvent> events, out int dispatched, out string? error)
        {
            error = null;
            dispatched = 0;
            var skipped = new List<string>();

            foreach (var ev in events)
            {
                switch ((ev.Type ?? "key").ToLowerInvariant())
                {
                    case "key":
                        if (ev.Key == null || !TryMapKey(ev.Key, out var vk))
                        {
                            skipped.Add($"key '{ev.Key}'");
                            continue;
                        }
                        if (ev.HoldMs > 0)
                        {
                            SendKey(vk, down: true);
                            Sleep(ev.HoldMs);
                            SendKey(vk, down: false);
                        }
                        else
                        {
                            SendKey(vk, down: ev.Pressed);
                            lock (_lock) { if (ev.Pressed) _heldVks.Add(vk); else _heldVks.Remove(vk); }
                        }
                        dispatched++;
                        break;

                    case "mouse_button":
                        if (!TryMapMouseButton(ev.Button, out var downFlag, out var upFlag))
                        {
                            skipped.Add($"button '{ev.Button}'");
                            continue;
                        }
                        if (ev.HoldMs > 0)
                        {
                            SendMouseButton(downFlag);
                            Sleep(ev.HoldMs);
                            SendMouseButton(upFlag);
                        }
                        else
                        {
                            SendMouseButton(ev.Pressed ? downFlag : upFlag);
                            lock (_lock) { if (ev.Pressed) _heldMouseUp.Add(upFlag); else _heldMouseUp.Remove(upFlag); }
                        }
                        dispatched++;
                        break;

                    case "mouse_move":
                        MoveCursorAbsolute((int)ev.X, (int)ev.Y);
                        dispatched++;
                        break;

                    case "action":
                        // Named engine actions have no OS-level equivalent.
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
            ushort[] vks;
            uint[] mouseUps;
            lock (_lock)
            {
                vks = new ushort[_heldVks.Count]; _heldVks.CopyTo(vks); _heldVks.Clear();
                mouseUps = new uint[_heldMouseUp.Count]; _heldMouseUp.CopyTo(mouseUps); _heldMouseUp.Clear();
            }
            foreach (var vk in vks) SendKey(vk, down: false);
            foreach (var up in mouseUps) SendMouseButton(up);
        }

        // ---- UI actions ----------------------------------------------------
        // Everything below drives a user interface rather than a game: it works in
        // virtual-screen pixels and it respects the timings the OS uses to tell one
        // gesture from another.

        public bool MoveTo(int screenX, int screenY, out string? error)
        {
            error = null;
            MoveCursorAbsolute(screenX, screenY);
            return true;
        }

        public bool Click(int screenX, int screenY, string button, int count, out string? error)
        {
            error = null;
            if (!TryMapMouseButton(button, out var down, out var up))
            {
                error = $"Unknown mouse button '{button}'.";
                return false;
            }

            count = Math.Max(1, Math.Min(3, count));
            MoveCursorAbsolute(screenX, screenY);
            // A control that highlights on hover needs a moment to notice the pointer
            // before the press, or the click lands on a state the screenshot never showed.
            Thread.Sleep(HoverSettleMs);

            for (int i = 0; i < count; i++)
            {
                if (i > 0) Thread.Sleep(MultiClickGapMs);
                Send(MouseInput(down), MouseInput(up)); // press and release, uninterruptible
            }
            return true;
        }

        public bool Drag(int fromX, int fromY, int toX, int toY, string button, out string? error)
        {
            error = null;
            if (!TryMapMouseButton(button, out var down, out var up))
            {
                error = $"Unknown mouse button '{button}'.";
                return false;
            }

            MoveCursorAbsolute(fromX, fromY);
            Thread.Sleep(HoverSettleMs);
            SendMouseButton(down);
            lock (_lock) _heldMouseUp.Add(up);
            try
            {
                Thread.Sleep(DragGrabMs);
                // Travel in steps: a press followed by a jump to the destination reads as a
                // click in the wrong place to anything that waits for the pointer to move.
                const int steps = 12;
                for (int i = 1; i <= steps; i++)
                {
                    var x = fromX + (toX - fromX) * i / (double)steps;
                    var y = fromY + (toY - fromY) * i / (double)steps;
                    MoveCursorAbsolute((int)Math.Round(x), (int)Math.Round(y));
                    Thread.Sleep(DragStepMs);
                }
                Thread.Sleep(DragGrabMs);
            }
            finally
            {
                SendMouseButton(up);
                lock (_lock) _heldMouseUp.Remove(up);
            }
            return true;
        }

        public bool Scroll(int screenX, int screenY, int clicks, bool horizontal, out string? error)
        {
            error = null;
            if (clicks == 0)
            {
                error = "Scroll amount is zero.";
                return false;
            }

            MoveCursorAbsolute(screenX, screenY);
            Thread.Sleep(HoverSettleMs);

            // One detent at a time: a single event carrying ten detents is coalesced by some
            // controls into far less than ten lines of movement.
            var perStep = clicks > 0 ? 1 : -1;
            for (int i = 0; i < Math.Abs(clicks); i++)
            {
                SendWheel(perStep * WHEEL_DELTA, horizontal);
                Thread.Sleep(ScrollStepMs);
            }
            return true;
        }

        public bool TypeText(string text, out string? error)
        {
            error = null;
            if (string.IsNullOrEmpty(text))
            {
                error = "No text to type.";
                return false;
            }

            // One character per SendInput call, its press and release together. Both halves
            // of that matter and each was measured: splitting press from release lets the
            // next character slip in between and the text scrambles ("ui_action reached the
            // keyboard: 1.5" arrived as "aaaaction aached     eeyboard:11.5"), while pushing
            // the whole string in as one block overruns the target's queue and every
            // character after the first few comes out as the last one ("AkerMcp kkkkkk...").
            foreach (var ch in text)
            {
                if (ch == '\r') continue; // a CRLF is one Enter, not two

                if (ch == '\n')
                    Send(KeyInput(0x0D, down: true), KeyInput(0x0D, down: false));
                else if (ch == '\t')
                    Send(KeyInput(0x09, down: true), KeyInput(0x09, down: false));
                else
                    // KEYEVENTF_UNICODE carries the character itself rather than a key
                    // position, so the text arrives the same under any keyboard layout —
                    // and characters with no virtual-key at all still arrive.
                    Send(UnicodeInput(ch, down: true), UnicodeInput(ch, down: false));

                Thread.Sleep(TypeGapMs);
            }
            return true;
        }

        public bool KeyChord(string chord, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(chord))
            {
                error = "No key to send.";
                return false;
            }

            // "+" separates the keys, so the plus key itself goes by its name: "ctrl++" split on
            // "+" was three parts, and the empty middle one was refused as "not a modifier".
            var spec = chord.Trim();
            if (spec == "+") spec = "plus";
            else if (spec.EndsWith("++", StringComparison.Ordinal)) spec = spec.Substring(0, spec.Length - 1) + "plus";
            var parts = spec.Split('+');
            var modifiers = new List<ushort>();
            ushort mainVk = 0;

            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i].Trim();
                // A trailing "+" means the plus key itself, e.g. "ctrl++".
                if (part.Length == 0) part = "+";

                bool isLast = i == parts.Length - 1;
                if (!isLast)
                {
                    if (!TryMapModifier(part, out var mod))
                    {
                        error = $"'{part}' is not a modifier. Write shortcuts as 'ctrl+shift+s', " +
                                "with ctrl/shift/alt/win first and the key last.";
                        return false;
                    }
                    modifiers.Add(mod);
                }
                else if (!TryMapKey(part, out mainVk))
                {
                    error = $"Unknown key '{part}'.";
                    return false;
                }
            }

            // Press in one call and release in another, each atomic: a modifier that gets
            // separated from its key is a shortcut that fires as a bare keystroke.
            var press = new List<INPUT>(modifiers.Count + 1);
            foreach (var mod in modifiers) press.Add(KeyInput(mod, down: true));
            press.Add(KeyInput(mainVk, down: true));

            var release = new List<INPUT>(modifiers.Count + 1) { KeyInput(mainVk, down: false) };
            for (int i = modifiers.Count - 1; i >= 0; i--) release.Add(KeyInput(modifiers[i], down: false));

            Send(press.ToArray());
            try { Thread.Sleep(ClickHoldMs); }
            finally { Send(release.ToArray()); }
            return true;
        }

        // Timings. Small enough that a sequence of actions stays responsive, large enough
        // that the OS and the widget on the other end read the gesture the way a hand on a
        // real mouse would produce it.
        private const int HoverSettleMs = 20;
        private const int ClickHoldMs = 25;
        private const int MultiClickGapMs = 60;   // well inside the default 500 ms double-click time
        private const int DragGrabMs = 60;
        private const int DragStepMs = 12;
        private const int ScrollStepMs = 25;
        /// <summary>
        /// Pause between typed characters. How fast a window can take synthetic text is a
        /// property of that window, not of this code, so this is measured rather than
        /// guessed: typing the alphabet plus digits into Windows 11 Notepad dropped a
        /// character at 12 ms and was exact at 25, 40 and 60. The default keeps a margin
        /// over the first value that worked; AKER_MCP_TYPE_GAP_MS raises it for a slower
        /// target, or lowers it when a long string into a Win32 window is worth the risk.
        /// </summary>
        private static readonly int TypeGapMs =
            int.TryParse(Environment.GetEnvironmentVariable("AKER_MCP_TYPE_GAP_MS"), out var gap)
            && gap >= 0 && gap <= 500 ? gap : 30;

        private static void Sleep(double ms)
        {
            var clamped = Math.Max(0, Math.Min(5000, ms)); // cap so a bad value can't hang the server
            if (clamped > 0) Thread.Sleep((int)clamped);
        }

        // ---- SendInput plumbing ----

        private const uint INPUT_MOUSE = 0;
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_SCANCODE = 0x0008;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint MAPVK_VK_TO_VSC = 0;

        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint MOUSEEVENTF_HWHEEL = 0x1000;
        private const int WHEEL_DELTA = 120;

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion U; }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk; public ushort wScan; public uint dwFlags;
            public uint time; public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx; public int dy; public uint mouseData; public uint dwFlags;
            public uint time; public IntPtr dwExtraInfo;
        }

        // Extended keys need the E0 prefix (KEYEVENTF_EXTENDEDKEY) so their scancode maps to
        // the dedicated key (e.g. VK_LEFT) rather than its numpad twin.
        private static bool IsExtendedKey(ushort vk)
        {
            switch (vk)
            {
                case 0x25: case 0x26: case 0x27: case 0x28: // arrows
                case 0x2D: case 0x2E:                        // Insert, Delete
                case 0x24: case 0x23: case 0x21: case 0x22: // Home, End, PageUp, PageDown
                case 0x5B: case 0x5C:                        // left/right Windows key
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Hand a block of events to the OS in one call. Everything that has to stay
        /// together — a press with its release, a modifier with its key — belongs in the
        /// same block: that is the only guarantee Windows gives that nothing else, the
        /// person at the keyboard included, gets in between.
        /// </summary>
        private static void Send(params INPUT[] inputs)
        {
            if (inputs.Length > 0)
                SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        private static INPUT KeyInput(ushort vk, bool down)
        {
            ushort scan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
            uint flags = KEYEVENTF_SCANCODE | (down ? 0u : KEYEVENTF_KEYUP);
            if (IsExtendedKey(vk)) flags |= KEYEVENTF_EXTENDEDKEY;

            return new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags }
                }
            };
        }

        private static INPUT UnicodeInput(char ch, bool down)
        {
            uint flags = KEYEVENTF_UNICODE | (down ? 0u : KEYEVENTF_KEYUP);
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = flags }
                }
            };
        }

        private static INPUT MouseInput(uint flags, int mouseData = 0) => new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { mouseData = unchecked((uint)mouseData), dwFlags = flags }
            }
        };

        private static void SendKey(ushort vk, bool down) => Send(KeyInput(vk, down));

        private static void SendMouseButton(uint flag) => Send(MouseInput(flag));

        private static void SendWheel(int delta, bool horizontal)
            => Send(MouseInput(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, delta));

        private static void MoveCursorAbsolute(int x, int y)
        {
            // The virtual screen is the bounding box of every monitor, and its origin is not
            // 0,0 when one sits left of or above the primary. MOUSEEVENTF_VIRTUALDESK tells
            // the OS to read the normalized coordinates against that box.
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
            int vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));

            // Aim at the middle of the target pixel, over 65536 steps rather than 65535.
            // Windows divides the range back by the screen width and truncates, so a value
            // computed for the pixel's left edge lands on the pixel before it about half the
            // time: measured, x=8 arrived as 7 until the half-pixel was added.
            double nx = (x - vx + 0.5) * 65536.0 / vw;
            double ny = (y - vy + 0.5) * 65536.0 / vh;

            var input = new INPUT
            {
                type = INPUT_MOUSE,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        // Clamped, because a point off the virtual screen has nowhere to go.
                        // A maximized window's client area starts a few pixels outside it, so
                        // its very first pixels resolve to the screen edge instead.
                        dx = (int)Math.Round(Math.Clamp(nx, 0, 65535)),
                        dy = (int)Math.Round(Math.Clamp(ny, 0, 65535)),
                        dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    }
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        private static bool TryMapMouseButton(string? button, out uint down, out uint up)
        {
            switch ((button ?? "left").ToLowerInvariant())
            {
                case "left": down = MOUSEEVENTF_LEFTDOWN; up = MOUSEEVENTF_LEFTUP; return true;
                case "right": down = MOUSEEVENTF_RIGHTDOWN; up = MOUSEEVENTF_RIGHTUP; return true;
                case "middle": down = MOUSEEVENTF_MIDDLEDOWN; up = MOUSEEVENTF_MIDDLEUP; return true;
                default: down = up = 0; return false;
            }
        }

        private static bool TryMapModifier(string name, out ushort vk)
        {
            switch (name.ToLowerInvariant())
            {
                case "ctrl": case "control": case "lctrl": vk = 0x11; return true;
                case "shift": case "lshift": vk = 0x10; return true;
                case "alt": case "lalt": vk = 0x12; return true;
                case "win": case "cmd": case "meta": case "super": vk = 0x5B; return true;
                default: vk = 0; return false;
            }
        }

        // Canonical, engine-neutral key names → Windows virtual-key codes.
        private static bool TryMapKey(string key, out ushort vk)
        {
            var k = key.Trim();
            // "{ESC}", "{ENTER}": the SendKeys spelling, which is how a dialog key is often
            // written down. Accepted rather than "Nothing dispatched" for the braces alone.
            if (k.Length > 2 && k[0] == '{' && k[k.Length - 1] == '}') k = k.Substring(1, k.Length - 2).Trim();
            if (k.Length == 0) k = key; // a lone space is the space bar, not an empty name
            if (k.Length == 0) { vk = 0; return false; }

            if (k.Length == 1)
            {
                char c = char.ToUpperInvariant(k[0]);
                if (c >= 'A' && c <= 'Z') { vk = c; return true; }
                if (c >= '0' && c <= '9') { vk = c; return true; }
            }

            switch (k.ToLowerInvariant())
            {
                case "space": case "spacebar": case " ": vk = 0x20; return true;
                case "enter": case "return": vk = 0x0D; return true;
                case "escape": case "esc": vk = 0x1B; return true;
                case "tab": vk = 0x09; return true;
                case "backspace": vk = 0x08; return true;
                case "delete": case "del": vk = 0x2E; return true;
                case "insert": case "ins": vk = 0x2D; return true;
                case "home": vk = 0x24; return true;
                case "end": vk = 0x23; return true;
                case "pageup": case "pgup": vk = 0x21; return true;
                case "pagedown": case "pgdn": vk = 0x22; return true;
                case "up": case "uparrow": vk = 0x26; return true;
                case "down": case "downarrow": vk = 0x28; return true;
                case "left": case "leftarrow": vk = 0x25; return true;
                case "right": case "rightarrow": vk = 0x27; return true;
                case "shift": case "lshift": vk = 0x10; return true;
                case "ctrl": case "control": case "lctrl": vk = 0x11; return true;
                case "alt": case "lalt": vk = 0x12; return true;
                case "win": case "cmd": case "meta": case "super": vk = 0x5B; return true;

                // Punctuation, by the US-layout OEM codes shortcuts are written against.
                case ",": case "comma": vk = 0xBC; return true;
                case ".": case "period": vk = 0xBE; return true;
                case "-": case "minus": vk = 0xBD; return true;
                case "+": case "=": case "plus": case "equals": vk = 0xBB; return true;
                case "/": case "slash": vk = 0xBF; return true;
                case "\\": case "backslash": vk = 0xDC; return true;
                case ";": case "semicolon": vk = 0xBA; return true;
                case "'": case "quote": vk = 0xDE; return true;
                case "[": case "leftbracket": vk = 0xDB; return true;
                case "]": case "rightbracket": vk = 0xDD; return true;
                case "`": case "backquote": case "tilde": vk = 0xC0; return true;
            }

            // F1-F24 (VK_F1 is 0x70 and they run consecutively).
            if ((k[0] == 'f' || k[0] == 'F') && k.Length <= 3
                && int.TryParse(k.Substring(1), out var fn) && fn >= 1 && fn <= 24)
            {
                vk = (ushort)(0x6F + fn);
                return true;
            }

            vk = 0;
            return false;
        }
    }
}
