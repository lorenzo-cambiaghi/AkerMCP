#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AkerMcp.Shared.Abstraction;
using Stride.Editor.EditorGame.Game;
using Stride.Engine;
using Stride.Graphics;

namespace AkerMcp.StrideAdapter
{
    /// <summary>
    /// Captures the Scene view by reading back the editor preview game's back buffer.
    ///
    /// IMPORTANT: Game Studio only ticks the embedded editor game when it needs to render
    /// a frame (user interaction / a dirty scene) — NOT merely when the window is focused
    /// (verified: focusing the window does not resume the script loop). So a scheduled
    /// readback only runs if the editor happens to be actively rendering. We therefore try
    /// the internal capture with a short timeout (clean scene-only image when it works) and
    /// otherwise return null so the Server falls back to OS-level window capture, which is
    /// the reliable path during MCP use.
    /// </summary>
    public class StrideScreenCapture : IScreenCapture
    {
        private static readonly TimeSpan ReadbackTimeout = TimeSpan.FromSeconds(3);

        private readonly object _gate = new();
        private TaskCompletionSource<byte[]?>? _queued;
        private Game? _queuedOn;
        private int _waiting;

        public (byte[] bytes, string contentType)? CaptureView(string viewType)
        {
            var game = StrideSceneBridge.ActiveEditorGame();
            if (game == null) return null;

            // A hidden editor game (its scene tab is not in front) draws nothing and runs
            // its scripts about once a second; a faulted one runs nothing. Waiting would
            // only stall the editor for the whole timeout to get no image, so fail at once:
            // null sends the Server to its OS-level capture.
            if (game is EditorServiceGame { IsEditorHidden: true } or EditorServiceGame { Faulted: true })
            {
                Diag("editor game hidden or faulted; using OS-level fallback");
                return null;
            }

            // Counted before anything is queued, so a readback that runs at once sees it.
            Interlocked.Increment(ref _waiting);
            try
            {
                TaskCompletionSource<byte[]?> pending;
                lock (_gate)
                {
                    // One readback queued at a time. While the editor is idle a queued one
                    // does not run; queueing another per call would pile them up, and
                    // they would all run, each saving a PNG, the moment rendering resumes.
                    if (_queued == null || _queued.Task.IsCompleted || !ReferenceEquals(_queuedOn, game))
                    {
                        _queued = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _queuedOn = game;
                        Schedule(game, _queued);
                    }
                    pending = _queued;
                }

                // This runs on the WPF thread: pump it while waiting instead of freezing
                // Game Studio for up to the whole timeout.
                if (!StrideUiWait.Wait(pending.Task, ReadbackTimeout))
                {
                    Diag("capture task did not run (editor idle); using OS-level fallback");
                    return null;
                }

                var bytes = pending.Task.Result;
                return bytes is { Length: > 0 } ? (bytes, "image/png") : null;
            }
            catch (Exception ex)
            {
                Diag($"wait failed: {ex.Message}");
                return null;
            }
            finally
            {
                Interlocked.Decrement(ref _waiting);
            }
        }

        // The immediate-context CommandList and a valid back buffer are only available on
        // the game thread, so the readback is scheduled there.
        private void Schedule(Game game, TaskCompletionSource<byte[]?> tcs)
        {
            game.Script.AddTask(() =>
            {
                try
                {
                    // Every caller gave up while this sat in the queue: nobody wants the
                    // image, so skip the readback rather than pay for it.
                    if (Volatile.Read(ref _waiting) == 0) { tcs.TrySetResult(null); return Task.CompletedTask; }

                    var backBuffer = game.GraphicsDevice?.Presenter?.BackBuffer;
                    if (backBuffer == null) { tcs.TrySetResult(null); return Task.CompletedTask; }

                    using var ms = new MemoryStream();
                    backBuffer.Save(game.GraphicsContext.CommandList, ms, ImageFileType.Png);
                    tcs.TrySetResult(ms.ToArray());
                }
                catch (Exception ex)
                {
                    Diag($"capture failed: {ex.GetType().Name}: {ex.Message}");
                    tcs.TrySetResult(null);
                }
                return Task.CompletedTask;
            });
        }

        private static void Diag(string msg) => StrideDiag.Log("screen-capture", msg);
    }
}
