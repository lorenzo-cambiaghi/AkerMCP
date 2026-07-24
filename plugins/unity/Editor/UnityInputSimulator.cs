#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEngine;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.Unity
{
    /// <summary>
    /// In-process input injection for Unity's **new Input System** (com.unity.inputsystem),
    /// via reflection so the adapter has NO compile-time dependency on the package: projects
    /// without it (or using only the legacy Input Manager) simply report Supported=false and
    /// the server falls back to OS-level SendInput. Fail-safe: any reflection error →
    /// Supported=false, never a throw that breaks send_input.
    ///
    /// The Input System takes ABSOLUTE state snapshots (a full keyboard/mouse state per event),
    /// so this tracks the currently-pressed keys/buttons and re-sends the whole state each time.
    /// `hold_ms` presses now and schedules a non-blocking release (a Timer marshalled back onto
    /// the editor main thread) so the running game sees the key held across real frames.
    /// </summary>
    public sealed class UnityInputSimulator : IInputSimulator
    {
        private readonly IMainThreadDispatcher _dispatcher;

        // Absolute state, mutated only on the main thread (SendInput and scheduled releases
        // both run there), so no locking is needed.
        private readonly HashSet<object> _pressedKeys = new HashSet<object>();   // boxed Key enum values
        private readonly HashSet<int> _mouseButtons = new HashSet<int>();
        private Vector2 _mousePos;
        private readonly List<Timer> _timers = new List<Timer>();

        public UnityInputSimulator(IMainThreadDispatcher dispatcher) => _dispatcher = dispatcher;

        public InputResult SendInput(IReadOnlyList<InputEvent> events)
        {
            var refl = Reflected.Instance;
            if (!refl.Available)
                return new InputResult
                {
                    Supported = false,
                    Note = "Unity's new Input System package isn't available; using OS-level input instead."
                };

            var skipped = new List<string>();
            int dispatched = 0;

            try
            {
                foreach (var ev in events)
                {
                    switch ((ev.Type ?? "key").ToLowerInvariant())
                    {
                        case "key":
                            if (ev.Key == null || !refl.TryParseKey(ev.Key, out var keyVal))
                            {
                                skipped.Add($"key '{ev.Key}'");
                                continue;
                            }
                            if (ApplyKey(refl, keyVal!, ev.Pressed, ev.HoldMs)) dispatched++;
                            else skipped.Add($"key '{ev.Key}' (no keyboard device)");
                            break;

                        case "mouse_button":
                            if (!TryMouseButtonBit(ev.Button, out var bit))
                            {
                                skipped.Add($"button '{ev.Button}'");
                                continue;
                            }
                            if (ApplyMouseButton(refl, bit, ev.Pressed, ev.HoldMs)) dispatched++;
                            else skipped.Add($"button '{ev.Button}' (no mouse device)");
                            break;

                        case "mouse_move":
                            // Input System screen space is bottom-left origin; the screenshots and
                            // OS-level path the AI reasons about are top-left. Flip Y so mouse_move
                            // x/y mean the same top-left pixels on both paths.
                            float flippedY = UnityEngine.Screen.height - (float)ev.Y;
                            _mousePos = new Vector2((float)ev.X, flippedY);
                            if (refl.QueueMouse(_mousePos, MouseButtonsMask())) dispatched++;
                            else skipped.Add("mouse_move (no mouse device)");
                            break;

                        case "action":
                            // Named actions can't be injected at the device level without the
                            // project's action asset; not supported on this path.
                            skipped.Add($"action '{ev.Action}'");
                            break;

                        default:
                            skipped.Add($"type '{ev.Type}'");
                            break;
                    }
                }
                // NOTE: deliberately DO NOT call InputSystem.Update() here. In Play Mode the
                // running player loop consumes the queued state events on its next frame, so the
                // press/release transition lands on a frame the game actually reads — which is what
                // makes wasPressedThisFrame / GetKeyDown / InputAction "performed" fire. Forcing an
                // extra update here would burn the transition on a non-game step and silently break
                // every one-shot input (jump, click).
            }
            catch (Exception ex)
            {
                // Fail safe: let the server fall back to OS-level input.
                return new InputResult
                {
                    Supported = false,
                    Note = $"Input System injection failed ({ex.GetType().Name}: {ex.Message}); using OS-level input."
                };
            }

            if (dispatched == 0)
                // Nothing landed (no active device — e.g. Active Input Handling = legacy — or all
                // events unmapped). Report Supported=false so the server's OS-level fallback engages
                // (OS-level SendInput to the focused Game View drives the legacy Input Manager too).
                return new InputResult
                {
                    Supported = false,
                    Success = false,
                    Note = skipped.Count > 0
                        ? $"No in-process injection ({string.Join(", ", skipped)}); using OS-level input."
                        : "No active input device for in-process injection; using OS-level input."
                };

            return new InputResult
            {
                Supported = true,
                Success = true,
                Dispatched = dispatched,
                Note = skipped.Count > 0 ? $"Skipped: {string.Join(", ", skipped)}." : null
            };
        }

        private bool ApplyKey(Reflected refl, object keyVal, bool pressed, double holdMs)
        {
            if (pressed) _pressedKeys.Add(keyVal); else _pressedKeys.Remove(keyVal);
            if (!refl.QueueKeyboard(_pressedKeys)) return false; // no device — report as not injected

            if (pressed && holdMs > 0)
                ScheduleRelease(holdMs, () =>
                {
                    _pressedKeys.Remove(keyVal);
                    refl.QueueKeyboard(_pressedKeys); // player loop consumes it next frame
                });
            return true;
        }

        private bool ApplyMouseButton(Reflected refl, int bit, bool pressed, double holdMs)
        {
            if (pressed) _mouseButtons.Add(bit); else _mouseButtons.Remove(bit);
            if (!refl.QueueMouse(_mousePos, MouseButtonsMask())) return false;

            if (pressed && holdMs > 0)
                ScheduleRelease(holdMs, () =>
                {
                    _mouseButtons.Remove(bit);
                    refl.QueueMouse(_mousePos, MouseButtonsMask()); // player loop consumes it next frame
                });
            return true;
        }

        private ushort MouseButtonsMask()
        {
            ushort mask = 0;
            foreach (var b in _mouseButtons) mask |= (ushort)(1 << b);
            return mask;
        }

        // Fire the release after holdMs on a Timer thread, then marshal it to the main thread.
        private void ScheduleRelease(double holdMs, Action release)
        {
            var ms = (int)Math.Max(1, Math.Min(10_000, holdMs));
            Timer? timer = null;
            timer = new Timer(_ =>
            {
                // release() runs later on the main thread; swallow there too so a stale
                // release (device gone / play exited) can't fault an unobserved task.
                try { _ = _dispatcher.RunOnMainThread(() => { try { release(); } catch { } return 0; }); }
                catch { /* editor tearing down / play mode exited — ignore */ }
                finally { lock (_timers) _timers.Remove(timer!); timer?.Dispose(); }
            }, null, ms, Timeout.Infinite);
            lock (_timers) _timers.Add(timer);
        }

        private static bool TryMouseButtonBit(string? button, out int bit)
        {
            switch ((button ?? "left").ToLowerInvariant())
            {
                case "left": bit = 0; return true;   // MouseButton.Left
                case "right": bit = 1; return true;  // MouseButton.Right
                case "middle": bit = 2; return true; // MouseButton.Middle
                default: bit = -1; return false;
            }
        }

        /// <summary>
        /// Lazily-resolved reflection handles for the Input System. Resolves once; if anything
        /// is missing, <see cref="Available"/> is false and callers fall back to OS-level.
        /// </summary>
        private sealed class Reflected
        {
            private static Reflected? _instance;
            public static Reflected Instance => _instance ??= new Reflected();

            public readonly bool Available;

            private readonly Type? _keyEnumType;
            private readonly PropertyInfo? _keyboardCurrentProp;
            private readonly PropertyInfo? _mouseCurrentProp;
            private readonly ConstructorInfo? _keyboardStateCtor;
            private readonly Type? _mouseStateType;
            private readonly FieldInfo? _mousePositionField;
            private readonly FieldInfo? _mouseButtonsField;
            private readonly MethodInfo? _queueKeyboard;   // QueueStateEvent<KeyboardState>
            private readonly MethodInfo? _queueMouse;      // QueueStateEvent<MouseState>

            private readonly Dictionary<string, object> _keyCache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            private Reflected()
            {
                try
                {
                    const string asm = "Unity.InputSystem";
                    var inputSystemType = Type.GetType($"UnityEngine.InputSystem.InputSystem, {asm}");
                    var keyboardType = Type.GetType($"UnityEngine.InputSystem.Keyboard, {asm}");
                    var mouseType = Type.GetType($"UnityEngine.InputSystem.Mouse, {asm}");
                    _keyEnumType = Type.GetType($"UnityEngine.InputSystem.Key, {asm}");
                    var keyboardStateType = Type.GetType($"UnityEngine.InputSystem.LowLevel.KeyboardState, {asm}");
                    _mouseStateType = Type.GetType($"UnityEngine.InputSystem.LowLevel.MouseState, {asm}");

                    if (inputSystemType == null || keyboardType == null || mouseType == null ||
                        _keyEnumType == null || keyboardStateType == null || _mouseStateType == null)
                        return;

                    // Resolve the device fresh per call (Keyboard.current can be null early or change).
                    _keyboardCurrentProp = keyboardType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                    _mouseCurrentProp = mouseType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);

                    _keyboardStateCtor = keyboardStateType.GetConstructor(new[] { _keyEnumType.MakeArrayType() });
                    _mousePositionField = _mouseStateType.GetField("position");
                    _mouseButtonsField = _mouseStateType.GetField("buttons");

                    var queueGeneric = inputSystemType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "QueueStateEvent" && m.IsGenericMethodDefinition
                                             && m.GetParameters().Length == 3);
                    if (queueGeneric != null)
                    {
                        _queueKeyboard = queueGeneric.MakeGenericMethod(keyboardStateType);
                        _queueMouse = queueGeneric.MakeGenericMethod(_mouseStateType);
                    }

                    Available = _keyboardCurrentProp != null && _mouseCurrentProp != null
                                && _keyboardStateCtor != null && _queueKeyboard != null
                                && _queueMouse != null && _mousePositionField != null && _mouseButtonsField != null;
                }
                catch
                {
                    Available = false;
                }
            }

            public bool TryParseKey(string canonical, out object? keyValue)
            {
                keyValue = null;
                if (_keyEnumType == null) return false;
                var enumName = CanonicalToKeyName(canonical);
                if (enumName == null) return false;
                if (_keyCache.TryGetValue(enumName, out var cached)) { keyValue = cached; return true; }
                try
                {
                    keyValue = Enum.Parse(_keyEnumType, enumName);
                    _keyCache[enumName] = keyValue;
                    return true;
                }
                catch { return false; }
            }

            public bool QueueKeyboard(HashSet<object> pressed)
            {
                var device = _keyboardCurrentProp!.GetValue(null);
                if (device == null) return false; // no keyboard device present
                var arr = Array.CreateInstance(_keyEnumType!, pressed.Count);
                int i = 0;
                foreach (var k in pressed) arr.SetValue(k, i++);
                var state = _keyboardStateCtor!.Invoke(new object[] { arr });
                _queueKeyboard!.Invoke(null, new object[] { device, state, -1.0 });
                return true;
            }

            public bool QueueMouse(Vector2 position, ushort buttons)
            {
                var device = _mouseCurrentProp!.GetValue(null);
                if (device == null) return false; // no mouse device present
                var state = Activator.CreateInstance(_mouseStateType!)!; // boxed struct
                _mousePositionField!.SetValue(state, position);
                _mouseButtonsField!.SetValue(state, buttons);
                _queueMouse!.Invoke(null, new object[] { device, state, -1.0 });
                return true;
            }

            // Canonical, engine-neutral names → Input System `Key` enum member names.
            private static string? CanonicalToKeyName(string key)
            {
                var k = key.Trim();
                if (k.Length == 1)
                {
                    char c = char.ToUpperInvariant(k[0]);
                    if (c >= 'A' && c <= 'Z') return c.ToString();       // Key.A .. Key.Z
                    if (c >= '0' && c <= '9') return "Digit" + c;        // Key.Digit0 .. Digit9
                }
                switch (k.ToLowerInvariant())
                {
                    case "space": case "spacebar": return "Space";
                    case "enter": case "return": return "Enter";
                    case "escape": case "esc": return "Escape";
                    case "tab": return "Tab";
                    case "backspace": return "Backspace";
                    case "delete": case "del": return "Delete";
                    case "up": case "uparrow": return "UpArrow";
                    case "down": case "downarrow": return "DownArrow";
                    case "left": case "leftarrow": return "LeftArrow";
                    case "right": case "rightarrow": return "RightArrow";
                    case "shift": case "lshift": return "LeftShift";
                    case "ctrl": case "control": case "lctrl": return "LeftCtrl";
                    case "alt": case "lalt": return "LeftAlt";
                    default: return null;
                }
            }
        }
    }
}
