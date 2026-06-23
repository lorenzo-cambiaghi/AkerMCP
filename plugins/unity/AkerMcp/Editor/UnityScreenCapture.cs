#nullable enable
using System.Reflection;
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

            // Preferred path: grab the actual rendered pixels of the SceneView panel,
            // which include gizmos, Handles and editor overlays. A plain cam.Render()
            // (the fallback below) omits all of those. If the internal API isn't found
            // (e.g. a future Unity version changed it) we degrade gracefully.
            var grabbed = CaptureSceneViewViaGrabPixels(sv);
            return grabbed ?? CaptureSceneViewViaCamera(sv);
        }

        // Reflects into EditorWindow.m_Parent (the GUIView/HostView that owns the panel's
        // on-screen pixels) and calls its internal GrabPixels(RenderTexture, Rect).
        private static byte[]? CaptureSceneViewViaGrabPixels(SceneView sv)
        {
            int w = (int)sv.position.width;
            int h = (int)sv.position.height;
            if (w <= 0 || h <= 0) return null;

            var parentField = typeof(EditorWindow).GetField("m_Parent",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var guiView = parentField?.GetValue(sv);
            if (guiView == null) return null;

            // GrabPixels is declared internal on the base GUIView, while m_Parent's runtime
            // type is a derived view (e.g. DockArea). Reflection does not surface inherited
            // non-public members, so walk the base-type chain to locate it.
            MethodInfo? grab = null;
            for (var t = guiView.GetType(); t != null && grab == null; t = t.BaseType)
            {
                grab = t.GetMethod("GrabPixels",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                    null, new[] { typeof(RenderTexture), typeof(Rect) }, null);
            }
            if (grab == null) return null;

            // Force a SYNCHRONOUS repaint so the grabbed framebuffer reflects the current
            // scene rather than a stale frame. sv.Repaint() only queues a repaint, so right
            // after a domain reload GrabPixels could capture geometry that renders during the
            // SceneView's render pass (e.g. GPU-instanced terrain) before it has drawn.
            // RepaintImmediately is internal, so reflect for it; fall back to the queued path.
            if (!TryRepaintImmediately(guiView))
                sv.Repaint();

            RenderTexture? rt = null;
            Texture2D? tex = null;
            var prevActive = RenderTexture.active;
            try
            {
                rt = new RenderTexture(w, h, 24);
                // Rect is in the view's local coordinates (origin at the panel's top-left).
                grab.Invoke(guiView, new object[] { rt, new Rect(0, 0, w, h) });

                RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);

                // GrabPixels' readback comes out mirrored left-to-right (UI text reads
                // backwards); flip it horizontally so the image matches what's on screen.
                FlipHorizontal(tex);
                tex.Apply();

                return tex.EncodeToPNG();
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[AkerMcp] SceneView GrabPixels capture failed, falling back to camera render: {ex.Message}");
                return null;
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (tex != null) Object.DestroyImmediate(tex);
                if (rt != null) Object.DestroyImmediate(rt);
            }
        }

        // Invokes the view's internal RepaintImmediately() (declared on GUIView) to render
        // the panel synchronously. Walks the base-type chain since it's an inherited
        // non-public member. Returns false if the API can't be found.
        private static bool TryRepaintImmediately(object guiView)
        {
            for (var t = guiView.GetType(); t != null; t = t.BaseType)
            {
                var m = t.GetMethod("RepaintImmediately",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                    null, System.Type.EmptyTypes, null);
                if (m != null)
                {
                    m.Invoke(guiView, null);
                    return true;
                }
            }
            return false;
        }

        // Mirrors the texture left-to-right in place (rows kept, columns reversed).
        private static void FlipHorizontal(Texture2D tex)
        {
            int w = tex.width, h = tex.height;
            var px = tex.GetPixels32();
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w / 2; x++)
                {
                    int a = row + x;
                    int b = row + (w - 1 - x);
                    (px[a], px[b]) = (px[b], px[a]);
                }
            }
            tex.SetPixels32(px);
        }

        // Fallback: render the SceneView camera directly. Captures the scene-view angle
        // but NOT gizmos/Handles/overlays (those are only drawn during the panel's GUI pass).
        private static byte[]? CaptureSceneViewViaCamera(SceneView sv)
        {
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
