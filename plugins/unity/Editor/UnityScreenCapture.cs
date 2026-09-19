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
            // Preferred path: read back the texture the Game view already displays. It holds exactly what the player
            // sees — UI Toolkit and uGUI overlays included — and it costs no extra render. The camera render below is
            // the fallback, and it had two faults the readback does not:
            //  - it draws the camera alone, so screen-space overlays never showed up in a screenshot;
            //  - it renders the camera OUTSIDE the player loop, into a target of another size. With the GPU Resident
            //    Drawer and GPU occlusion culling on, that render tests visibility against the depth of the last game
            //    frame: measured on a URP scene, a screenshot showed trees and houses missing while their shadows
            //    stayed on the ground, and 40 consecutive frames of the real view captured at the same moment
            //    (ScreenCapture) showed nothing missing.
            return CaptureGameViewTarget() ?? CaptureGameViewViaCamera();
        }

        // Reflects into PlayModeView.m_TargetTexture: the render target the game draws into, and the Game view shows.
        // Returns null when the field is not found (a future Unity version) or the view has not rendered yet.
        private static byte[]? CaptureGameViewTarget()
        {
            var gameViewType = System.Type.GetType("UnityEditor.GameView,UnityEditor");
            if (gameViewType == null) return null;
            var views = Resources.FindObjectsOfTypeAll(gameViewType);
            if (views.Length == 0) return null;
            var view = views[0];

            FieldInfo? field = null;
            for (var t = gameViewType; t != null && field == null; t = t.BaseType)
                field = t.GetField("m_TargetTexture", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field == null) return null;

            // Only a Game view that is ON SCREEN keeps its target current. Docked behind another tab (the usual layout
            // outside Play Mode: Scene in front, Game behind) the target is not there at all — measured null — and
            // repainting the host would repaint the tab in front. Then the camera render is the only way.
            var parentField = typeof(EditorWindow).GetField("m_Parent", BindingFlags.NonPublic | BindingFlags.Instance);
            var guiView = parentField?.GetValue(view);
            if (guiView == null || !IsFrontTab(guiView, view)) return null;

            // Outside Play Mode the Game view renders on repaint only: make it draw the current scene first, as the
            // SceneView path does. In Play Mode the player loop has already rendered the frame.
            if (!Application.isPlaying && !TryRepaintImmediately(guiView))
                ((EditorWindow)view).Repaint();

            if (!(field.GetValue(view) is RenderTexture target) || !target.IsCreated()) return null;

            Texture2D? tex = null;
            var prevActive = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                tex = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                // Same convention as GrabPixels below, measured again here: on D3D12 the readback of the target came out
                // upside down (a panel's bottom bar at the top of the image, its title at the bottom).
                if (SystemInfo.graphicsUVStartsAtTop)
                    FlipVertical(tex);
                tex.Apply();
                return tex.EncodeToPNG();
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[AkerMcp] GameView target capture failed, falling back to camera render: {ex.Message}");
                return null;
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (tex != null) Object.DestroyImmediate(tex);
            }
        }

        // True when the window is the tab its host is showing. The host (a DockArea, or a single-view HostView) exposes the
        // shown window as its internal actualView; walk the base types, since reflection does not surface inherited
        // non-public members. When the property is missing, assume shown and let the target checks decide.
        private static bool IsFrontTab(object host, Object window)
        {
            for (var t = host.GetType(); t != null; t = t.BaseType)
            {
                var actual = t.GetProperty("actualView",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (actual != null) return ReferenceEquals(actual.GetValue(host), window);
            }
            return true;
        }

        // Fallback: render the main camera directly. Misses screen-space UI, and see CaptureGameView for why it can
        // disagree with the Game view when GPU occlusion culling is on.
        private static byte[]? CaptureGameViewViaCamera()
        {
            var cam = Camera.main;
            if (cam == null) cam = Object.FindAnyObjectByType<Camera>();
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

                // GrabPixels writes the panel into the RenderTexture using the graphics API's UV
                // convention, while ReadPixels/GetPixels32 always index rows from the BOTTOM. On
                // APIs whose UV origin is at the top (D3D11/D3D12/Metal/consoles,
                // graphicsUVStartsAtTop == true) the two disagree, so the readback comes out
                // upside down and has to be flipped; on OpenGL/GLES both start at the bottom and
                // it is already correct.
                //
                // Measured, not assumed: on D3D12 the same view was captured through GrabPixels
                // and through a direct camera render, and compared row by row. Matching the rows
                // straight scored 1,092,529 of difference against 600,375 when matching them
                // mirrored — the grab was upside down. The condition used to be the other way
                // round, which is why every screenshot came back rotated. (An even earlier
                // unconditional FlipHorizontal was wrong in a different way: it mirrored the image
                // left-to-right, making UI text read backwards.)
                if (SystemInfo.graphicsUVStartsAtTop)
                    FlipVertical(tex);
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

        // Mirrors the texture top-to-bottom in place (whole rows swapped). Used to correct the
        // RenderTexture readback on graphics APIs whose UV origin is at the bottom (OpenGL/GLES).
        private static void FlipVertical(Texture2D tex)
        {
            int w = tex.width, h = tex.height;
            var px = tex.GetPixels32();
            var tmp = new Color32[w];
            for (int y = 0; y < h / 2; y++)
            {
                int top = y * w;
                int bot = (h - 1 - y) * w;
                System.Array.Copy(px, top, tmp, 0, w);
                System.Array.Copy(px, bot, px, top, w);
                System.Array.Copy(tmp, 0, px, bot, w);
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
