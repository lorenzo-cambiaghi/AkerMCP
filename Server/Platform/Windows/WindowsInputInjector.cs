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
    /// window currently has the foreground focus, so the caller (ToolRegistry.send_input)
    /// focuses the target game/engine window first. Canonical key names (Space, Enter,
    /// arrows, letters, digits, modifiers) map to virtual-key codes; keys are sent with
    /// their scancode + the extended-key flag so arrow/edit keys reach games that read
    /// physical scancodes / raw input, not just message-queue (VK) consumers.
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
        private const uint MAPVK_VK_TO_VSC = 0;

        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

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
                    return true;
                default:
                    return false;
            }
        }

        private static void SendKey(ushort vk, bool down)
        {
            ushort scan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
            uint flags = KEYEVENTF_SCANCODE | (down ? 0u : KEYEVENTF_KEYUP);
            if (IsExtendedKey(vk)) flags |= KEYEVENTF_EXTENDEDKEY;

            var input = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags }
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        private static void SendMouseButton(uint flag)
        {
            var input = new INPUT
            {
                type = INPUT_MOUSE,
                U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flag } }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        private static void MoveCursorAbsolute(int x, int y)
        {
            int sw = Math.Max(1, GetSystemMetrics(SM_CXSCREEN));
            int sh = Math.Max(1, GetSystemMetrics(SM_CYSCREEN));
            var input = new INPUT
            {
                type = INPUT_MOUSE,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = (int)(x * 65535.0 / sw),
                        dy = (int)(y * 65535.0 / sh),
                        dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
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

        // Canonical, engine-neutral key names → Windows virtual-key codes.
        private static bool TryMapKey(string key, out ushort vk)
        {
            var k = key.Trim();
            if (k.Length == 1)
            {
                char c = char.ToUpperInvariant(k[0]);
                if (c >= 'A' && c <= 'Z') { vk = c; return true; }
                if (c >= '0' && c <= '9') { vk = c; return true; }
            }

            switch (k.ToLowerInvariant())
            {
                case "space": case "spacebar": vk = 0x20; return true;
                case "enter": case "return": vk = 0x0D; return true;
                case "escape": case "esc": vk = 0x1B; return true;
                case "tab": vk = 0x09; return true;
                case "backspace": vk = 0x08; return true;
                case "delete": case "del": vk = 0x2E; return true;
                case "up": case "uparrow": vk = 0x26; return true;
                case "down": case "downarrow": vk = 0x28; return true;
                case "left": case "leftarrow": vk = 0x25; return true;
                case "right": case "rightarrow": vk = 0x27; return true;
                case "shift": case "lshift": vk = 0x10; return true;
                case "ctrl": case "control": case "lctrl": vk = 0x11; return true;
                case "alt": case "lalt": vk = 0x12; return true;
                default: vk = 0; return false;
            }
        }
    }
}
