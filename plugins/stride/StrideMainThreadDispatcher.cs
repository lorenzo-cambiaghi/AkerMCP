#nullable enable
using System.Windows.Threading;
using AkerMcp.Client;

namespace AkerMcp.StrideAdapter
{
    /// <summary>
    /// Game Studio is a WPF app, so scene/editor state must be touched on the UI
    /// thread. IPC requests arrive on background threads and enqueue actions in
    /// the base class; we drain the queue via the WPF <see cref="Dispatcher"/>.
    /// </summary>
    public sealed class StrideMainThreadDispatcher : MainThreadDispatcherBase
    {
        private readonly Dispatcher _dispatcher;

        public StrideMainThreadDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

        protected override void ScheduleProcessQueue()
            => _dispatcher.InvokeAsync(ProcessQueue);
    }
}
