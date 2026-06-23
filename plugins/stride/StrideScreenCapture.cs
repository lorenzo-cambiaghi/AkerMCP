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
    /// Captures the actual Scene view of the active Game Studio editor by reading
    /// back the editor preview game's back buffer and encoding it to PNG via
    /// <see cref="Texture.Save(CommandList, Stream, ImageFileType)"/>.
    ///
    /// The readback must run on the game thread (the immediate CommandList is only
    /// valid there), so we schedule it via the game's ScriptSystem and block the
    /// caller until it completes. The editor preview is the "scene" view; Stride's
    /// editor has no separate game view, so both viewTypes map to it.
    /// </summary>
    public class StrideScreenCapture : IScreenCapture
    {
        public (byte[] bytes, string contentType)? CaptureView(string viewType)
        {
            var game = StrideSceneBridge.ActiveEditorGame();
            if (game == null) return null;

            var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Run on the game thread: the immediate-context CommandList and a valid
            // back buffer are only available there.
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
                    Diag($"capture failed: {ex.Message}");
                    tcs.TrySetResult(null);
                }
                await Task.CompletedTask;
            });

            try
            {
                if (!tcs.Task.Wait(8000)) { Diag("capture timed out"); return null; }
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
