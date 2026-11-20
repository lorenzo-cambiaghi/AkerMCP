#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace AkerMcp.StrideAdapter
{
    /// <summary>
    /// Waits for a task from inside a call that runs on Game Studio's WPF thread, without
    /// freezing the editor and without deadlocking on work that needs that same thread.
    ///
    /// The adapter's interfaces are synchronous and the IPC handlers run them on the WPF
    /// thread, but what they wait for is not: Game Studio opens an editor and saves the
    /// session through async code whose continuations come back to that thread, and a
    /// build or a viewport readback runs elsewhere for seconds or minutes. Blocking on
    /// <c>Task.Wait</c> either deadlocks (the continuation can never run) or freezes the
    /// UI for the whole wait. So the wait pumps the dispatcher with a nested frame, the
    /// way a modal dialog does: Game Studio keeps painting and its continuations run.
    /// Other dispatcher work (including queued AkerMcp requests) can run during the wait,
    /// exactly as it does while any Game Studio dialog is open.
    /// </summary>
    internal static class StrideUiWait
    {
        /// <summary>True if <paramref name="task"/> completed within <paramref name="timeout"/>.</summary>
        public static bool Wait(Task task, TimeSpan timeout)
        {
            if (task.IsCompleted) return true;

            if (Dispatcher.FromThread(Thread.CurrentThread) == null)
                return task.Wait(timeout);   // not on a UI thread: blocking is harmless

            var frame = new DispatcherFrame();
            // Both callbacks only clear the flag; DispatcherFrame.Continue is free-threaded.
            using var timer = new Timer(_ => frame.Continue = false, null, timeout, Timeout.InfiniteTimeSpan);
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            try
            {
                Dispatcher.PushFrame(frame);
            }
            catch (InvalidOperationException)
            {
                // Dispatcher processing is suspended (a layout pass is under us): a nested
                // frame is not allowed here. Report what we have rather than block the UI.
            }
            return task.IsCompleted;
        }
    }
}
