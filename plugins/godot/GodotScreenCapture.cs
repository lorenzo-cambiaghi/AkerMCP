#if TOOLS
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
            var editor = EditorInterface.Singleton;
            var view = (viewType ?? "scene").ToLowerInvariant();

            // A running game lives in its own process and window; the editor viewport keeps showing
            // the edited scene. Answering "game" with it handed the model a picture of the wrong
            // thing, labelled as the game. Null sends the server to its OS-level capture instead.
            if (view == "game" && editor.IsPlayingScene())
                return null;

            // The editor has no separate "game" view, so "game" (not playing) and "scene" pick the
            // viewport the edited scene is drawn in. Always taking the 3D one returned an empty 3D
            // view for every 2D scene. "2d"/"3d" stay explicit overrides.
            bool use2D = view switch
            {
                "2d" => true,
                "3d" => false,
                _ => Is2DScene(editor.GetEditedSceneRoot())
            };

            SubViewport? vp = use2D ? editor.GetEditorViewport2D() : editor.GetEditorViewport3D(0);

            // The Image wraps a native copy of the whole frame; disposing it frees that now rather
            // than whenever the GC finalizes the wrapper.
            using var img = vp?.GetTexture()?.GetImage();
            if (img == null) return null;

            var png = img.SavePngToBuffer();
            return png is { Length: > 0 } ? (png, "image/png") : null;
        }

        // A scene is 2D when its root draws on the canvas (Node2D, Control), or when it is a plain
        // Node whose children do and none of which is 3D.
        private static bool Is2DScene(Node? root)
        {
            if (root == null) return false;
            if (root is Node3D) return false;
            if (root is CanvasItem) return true;

            bool any2D = false;
            foreach (Node child in root.GetChildren())
            {
                if (child is Node3D) return false;
                if (child is CanvasItem) any2D = true;
            }
            return any2D;
        }
    }
}
#endif
