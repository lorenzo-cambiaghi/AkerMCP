#nullable enable
using AkerMcp.Client;

namespace AkerMcp.GodotAdapter
{
    /// <summary>
    /// Godot is single-threaded for scene-tree access. IPC requests arrive on
    /// background threads and enqueue actions here; <see cref="AkerMcpEditorPlugin._Process"/>
    /// drains them on the main thread every frame, so there is nothing to
    /// schedule — the editor loop already ticks <see cref="MainThreadDispatcherBase.ProcessQueue"/>.
    /// </summary>
    public class GodotMainThreadDispatcher : MainThreadDispatcherBase
    {
        protected override void ScheduleProcessQueue() { /* pumped by EditorPlugin._Process */ }
    }
}
