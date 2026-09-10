using System.Collections.Generic;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.Server.Platform
{
    /// <summary>
    /// OS-level synthetic input injection. Used as the fallback for send_input when an
    /// engine adapter does not implement IInputSimulator (or reports it can't inject
    /// in-process) — mirrors the OS-level screenshot fallback. Events go to the current
    /// foreground window, so the caller focuses the target window first. One impl per OS.
    ///
    /// The <see cref="Inject"/> path speaks the engine-neutral <see cref="InputEvent"/>
    /// vocabulary a game understands (a key, a button, a position). The methods below it
    /// are the vocabulary a user interface understands — a click at a point, a drag, a
    /// wheel, a typed string, a shortcut — and they exist because composing those out of
    /// InputEvents loses what makes them work: the double-click interval, the intermediate
    /// motion a drag needs to be seen as a drag, and the Unicode path that types a
    /// character no virtual-key maps to. They are OS-level only: the ui_action tool drives
    /// the editor's own interface, which no in-process engine simulator can reach.
    /// </summary>
    public interface IPlatformInput
    {
        /// <summary>
        /// Dispatch the events in order to the foreground window. <paramref name="dispatched"/>
        /// reports how many were actually delivered (events with no OS-level mapping, e.g. a
        /// named action, are skipped). Returns false (with <paramref name="error"/> set) if
        /// nothing could be dispatched.
        /// </summary>
        bool Inject(IReadOnlyList<InputEvent> events, out int dispatched, out string? error);

        /// <summary>Move the cursor to a virtual-screen pixel.</summary>
        bool MoveTo(int screenX, int screenY, out string? error);

        /// <summary>
        /// Click at a virtual-screen pixel. <paramref name="count"/> above 1 sends the
        /// repeats close enough together for the OS to read them as a double or triple click.
        /// </summary>
        bool Click(int screenX, int screenY, string button, int count, out string? error);

        /// <summary>
        /// Press at the first point, move through it in steps, release at the second. The
        /// intermediate motion matters: a down-then-teleport-then-up is ignored by most
        /// drag handling, which waits to see the pointer travel.
        /// </summary>
        bool Drag(int fromX, int fromY, int toX, int toY, string button, out string? error);

        /// <summary>
        /// Turn the wheel at a virtual-screen pixel. <paramref name="clicks"/> is in wheel
        /// detents, positive meaning up (or right, when <paramref name="horizontal"/>).
        /// </summary>
        bool Scroll(int screenX, int screenY, int clicks, bool horizontal, out string? error);

        /// <summary>
        /// Type a string into whatever has keyboard focus, character by character, including
        /// characters that have no virtual-key of their own.
        /// </summary>
        bool TypeText(string text, out string? error);

        /// <summary>
        /// Send one shortcut: modifiers held while a key is pressed and released, written
        /// as "ctrl+s", "ctrl+shift+n", "alt+F4" or a bare key name such as "Escape".
        /// </summary>
        bool KeyChord(string chord, out string? error);

        /// <summary>
        /// Release every key/mouse button this injector pressed without a matching release
        /// (from an unbalanced pressed:true). Called on exit_play so a forgotten key-down
        /// can't stay physically stuck at the OS level and corrupt later input. No-op if
        /// nothing is held.
        /// </summary>
        void ReleaseAll();
    }
}
