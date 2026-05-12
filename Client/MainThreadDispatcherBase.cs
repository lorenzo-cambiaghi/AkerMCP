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
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            _queue.Enqueue(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(ct);
                    return;
                }

                try
                {
                    var result = action();
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            ScheduleProcessQueue();

            return tcs.Task;
        }

        public Task RunOnMainThread(Action action, CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _queue.Enqueue(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(ct);
                    return;
                }

                try
                {
                    action();
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            ScheduleProcessQueue();

            return tcs.Task;
        }

        public void ProcessQueue()
        {
            while (_queue.TryDequeue(out var action))
            {
                action();
            }
        }

        protected abstract void ScheduleProcessQueue();
    }
}
