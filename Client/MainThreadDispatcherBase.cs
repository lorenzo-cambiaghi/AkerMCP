using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.Client
{
    public abstract class MainThreadDispatcherBase : IMainThreadDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        public Task<T> RunOnMainThread<T>(Func<T> action, CancellationToken ct = default)
        {
            if (ct.IsCancellationRequested) return Task.FromCanceled<T>(ct);

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            // The caller is released as soon as its token fires, even when the main thread is held by a modal
            // dialog and the queue is not being pumped: the request's connection has gone, and the thread waiting
            // here should not wait with it. The queued action then finds the token cancelled and does not run.
            var registration = ct.CanBeCanceled
                ? ct.Register(() => tcs.TrySetCanceled(ct))
                : default;

            _queue.Enqueue(() =>
            {
                try
                {
                    if (ct.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled(ct);
                        return;
                    }

                    try
                    {
                        tcs.TrySetResult(action());
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                }
                finally
                {
                    registration.Dispose();
                }
            });

            ScheduleProcessQueue();

            return tcs.Task;
        }

        public Task RunOnMainThread(Action action, CancellationToken ct = default)
            => RunOnMainThread(() => { action(); return true; }, ct);

        public void ProcessQueue()
        {
            // Only what was queued when this pass started: work that keeps arriving while the queue is pumped is
            // left for the next editor tick instead of holding this one.
            for (int n = _queue.Count; n > 0 && _queue.TryDequeue(out var action); n--)
            {
                action();
            }
        }

        protected abstract void ScheduleProcessQueue();
    }
}
