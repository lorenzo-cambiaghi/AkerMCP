#nullable enable
using Godot;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.GodotAdapter
{
    /// <summary>
    /// Captures the editor's viewport render buffer. Called on the main thread by
    /// the IPC handler. If it returns null, the server falls back to OS-level
    /// window capture (Windows PrintWindow / macOS Quartz).
    /// </summary>
    public class GodotScreenCapture : IScreenCapture
    {
        public (byte[] bytes, string contentType)? CaptureView(string viewType)
        {
            // The editor has no separate "game" view; map "game"/"scene" to the 3D
            // viewport and allow "2d" explicitly for 2D scenes.
            SubViewport? vp = viewType == "2d"
                ? EditorInterface.Singleton.GetEditorViewport2D()
                : EditorInterface.Singleton.GetEditorViewport3D(0);

            var tex = vp?.GetTexture();
            var img = tex?.GetImage();
            if (img == null) return null;

            var png = img.SavePngToBuffer();
            return png is { Length: > 0 } ? (png, "image/png") : null;
        }
    }
}
