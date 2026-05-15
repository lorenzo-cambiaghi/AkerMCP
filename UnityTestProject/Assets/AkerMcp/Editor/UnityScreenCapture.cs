#nullable enable
using UnityEngine;
using UnityEditor;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.Unity
{
    public class UnityScreenCapture : IScreenCapture
    {
        public (byte[] bytes, string contentType)? CaptureView(string viewType)
        {
            byte[]? png = viewType switch
            {
                "scene" => CaptureSceneView(),
                _       => CaptureGameView(),
            };
            return png != null ? (png, "image/png") : null;
        }

        private byte[]? CaptureGameView()
        {
            // ScreenCapture.CaptureScreenshotAsTexture works in both play and edit modes
            // and grabs whatever GameView is currently rendering.
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            if (tex == null) return null;
            try { return tex.EncodeToPNG(); }
            finally { Object.DestroyImmediate(tex); }
        }

        private byte[]? CaptureSceneView()
        {
            var sv = SceneView.lastActiveSceneView;
            if (sv == null) return null;

            var cam = sv.camera;
            int w = (int)sv.position.width;
            int h = (int)sv.position.height;
            if (w <= 0 || h <= 0) return null;

            RenderTexture? rt = null;
            Texture2D? tex = null;
            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;
            try
            {
                rt = new RenderTexture(w, h, 24);
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();

                return tex.EncodeToPNG();
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                if (tex != null) Object.DestroyImmediate(tex);
                if (rt != null) Object.DestroyImmediate(rt);
            }
        }
    }
}
