using System.Collections.Generic;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.Server.Platform
{
    /// <summary>
    /// OS-level synthetic input injection. Used as the fallback for send_input when an
    /// engine adapter does not implement IInputSimulator (or reports it can't inject
    /// in-process) — mirrors the OS-level screenshot fallback. Events go to the current
    /// foreground window, so the caller focuses the target window first. One impl per OS.
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

        /// <summary>
        /// Release every key/mouse button this injector pressed without a matching release
        /// (from an unbalanced pressed:true). Called on exit_play so a forgotten key-down
        /// can't stay physically stuck at the OS level and corrupt later input. No-op if
        /// nothing is held.
        /// </summary>
        void ReleaseAll();
    }
}
