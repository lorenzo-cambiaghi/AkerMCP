using System.Collections.Generic;

namespace AkerMcp.Shared.Abstraction
{
    /// <summary>
    /// Optional. Engine adapters that implement this can inject synthetic input into a
    /// running game IN-PROCESS (highest fidelity: no window focus needed, deterministic).
    /// When absent — or when it reports <see cref="InputResult.Supported"/> = false — the
    /// server falls back to OS-level input injection to the engine/game window (mirrors
    /// the take_screenshot engine-internal / OS-level hybrid). Powers the send_input tool.
    /// </summary>
    public interface IInputSimulator
    {
        InputResult SendInput(IReadOnlyList<InputEvent> events);
    }

    /// <summary>
    /// One engine-neutral input event. Coordinates and key names are canonical strings so
    /// the same event maps to any engine (or to OS virtual keys in the fallback path).
    /// </summary>
    public class InputEvent
    {
        /// <summary>"key" | "mouse_button" | "mouse_move" | "action".</summary>
        public string Type { get; set; } = "key";
        /// <summary>Canonical key name, e.g. "Space", "Enter", "Escape", "Up"/"Down"/"Left"/"Right", "W".</summary>
        public string? Key { get; set; }
        /// <summary>"left" | "right" | "middle" for mouse_button.</summary>
        public string? Button { get; set; }
        /// <summary>Named input action / axis (Godot action, Unity Input System action).</summary>
        public string? Action { get; set; }
        /// <summary>True = press/down, false = release/up.</summary>
        public bool Pressed { get; set; } = true;
        /// <summary>Target position for mouse_move.</summary>
        public double X { get; set; }
        public double Y { get; set; }
        /// <summary>Convenience: press then auto-release after this many ms (0 = leave as-is).</summary>
        public double HoldMs { get; set; }
    }

    public class InputResult
    {
        /// <summary>False when in-process injection isn't available; the server then tries the OS-level path.</summary>
        public bool Supported { get; set; } = true;
        public bool Success { get; set; }
        /// <summary>How many events were actually dispatched.</summary>
        public int Dispatched { get; set; }
        public string? Error { get; set; }
        public string? Note { get; set; }
    }
}
