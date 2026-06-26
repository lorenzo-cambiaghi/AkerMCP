#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using AkerMcp.Shared.Abstraction;
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
        public (byte[] bytes, string contentType)? CaptureView(string viewType)
        {
            var game = StrideSceneBridge.ActiveEditorGame();
            if (game == null) return null;

            var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);

            // The immediate-context CommandList and a valid back buffer are only available
            // on the game thread, so schedule the readback there.
            game.Script.AddTask(async () =>
            {
                try
                {
                    var backBuffer = game.GraphicsDevice?.Presenter?.BackBuffer;
                    if (backBuffer == null) { tcs.TrySetResult(null); return; }

                    using var ms = new MemoryStream();
                    backBuffer.Save(game.GraphicsContext.CommandList, ms, ImageFileType.Png);
                    tcs.TrySetResult(ms.ToArray());
                }
                catch (Exception ex)
                {
                    Diag($"capture failed: {ex.GetType().Name}: {ex.Message}");
                    tcs.TrySetResult(null);
                }
                await Task.CompletedTask;
            });

            try
            {
                // Fail fast: if the editor isn't actively rendering, the task won't run.
                // Let the Server's OS-level fallback take over rather than stalling.
                if (!tcs.Task.Wait(3000)) { Diag("capture task did not run (editor idle); using OS-level fallback"); return null; }
            }
            catch (Exception ex) { Diag($"wait failed: {ex.Message}"); return null; }

            var bytes = tcs.Task.Result;
            return bytes is { Length: > 0 } ? (bytes, "image/png") : null;
        }

        private static void Diag(string msg)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "akermcp-stride.log"),
                    $"{DateTime.Now:HH:mm:ss} [screen-capture] {msg}{Environment.NewLine}");
            }
            catch { /* never throw from diagnostics */ }
        }
    }
}
