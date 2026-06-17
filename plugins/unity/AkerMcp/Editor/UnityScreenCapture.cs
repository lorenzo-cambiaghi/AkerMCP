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
            var cam = Camera.main;
            if (cam == null) cam = Object.FindFirstObjectByType<Camera>();
            if (cam == null) return null;

            int w = cam.pixelWidth > 0 ? cam.pixelWidth : 1024;
            int h = cam.pixelHeight > 0 ? cam.pixelHeight : 768;

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
            catch (System.Exception ex)
            {
                Debug.LogError($"[AkerMcp] GameView capture failed: {ex.Message}");
                return null;
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                if (tex != null) Object.DestroyImmediate(tex);
                if (rt != null) Object.DestroyImmediate(rt);
            }
        }

        private byte[]? CaptureSceneView()
        {
            var sv = SceneView.lastActiveSceneView;
            if (sv == null) sv = SceneView.sceneViews.Count > 0 ? (SceneView)SceneView.sceneViews[0] : null;
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
                
                // Ensure the scene view camera has all its settings
                sv.Repaint();
                cam.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();

                return tex.EncodeToPNG();
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[AkerMcp] SceneView capture failed: {ex.Message}");
                return null;
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
