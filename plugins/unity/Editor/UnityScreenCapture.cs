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
            // ⚠️ Known limit, measured on Unity 6000.6 / D3D12: with a URP asset that renders straight to the final target
            // (HDR off, no effect asking for an intermediate texture) the Game view target read here, outside the frame, holds
            // no image — nearly black, with the Stats overlay on top — while the Game view on screen is right; ScreenCapture in
            // Play Mode fails the same way (flat cyan). A faithful read would have to happen inside the frame, and this capture
            // is synchronous. The readback came back as a success all the same, a black picture where v1.5.0's camera render
            // showed the scene; so a readback that is nearly one colour is checked against the camera render below.
            var target = CaptureGameViewTarget(out float targetFlatness);
            if (target == null) return CaptureGameViewViaCamera(out _);
            if (targetFlatness < SuspectFlatness) return target;

            // Nearly one colour: either the frame is flat (a solid clear colour, a menu on a plain background) or the target
            // held no image. The camera render tells the two apart. When it sees clearly more than the target did, the target
            // was empty; when it is as flat, the readback — which carries the UI — is the better picture of the two.
            var camera = CaptureGameViewViaCamera(out float cameraFlatness);
            return camera != null && cameraFlatness < targetFlatness - FlatnessMargin ? camera : target;
        }

        // A readback with at least this share of samples on one colour is checked against the camera render.
        private const float SuspectFlatness = 0.9f;
        // How much less flat the camera render must be to replace the readback.
        private const float FlatnessMargin = 0.15f;

        // Reads back PlayModeView.m_TargetTexture: the render target the game draws into, and the Game view shows. Returns
        // null — and the camera render takes over — when no Game view is in front, when the internals are not where this
        // looks (another Unity version: every reflective step is inside the try, so a changed API degrades instead of
        // failing the screenshot), or when the view has not rendered yet.
        private static byte[]? CaptureGameViewTarget(out float flatness)
        {
            flatness = 0f;
            Texture2D? tex = null;
            var prevActive = RenderTexture.active;
            try
            {
                var target = FindGameViewTarget();
                if (target == null) return null;

                RenderTexture.active = target;
                tex = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                // Same convention as GrabPixels below, measured again here: on D3D12 the readback of the target came out
                // upside down (a panel's bottom bar at the top of the image, its title at the bottom).
                if (SystemInfo.graphicsUVStartsAtTop)
                    FlipVertical(tex);
                tex.Apply();
                flatness = Flatness(tex);
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

        // The render target of the Game view on screen. Of the Game views whose tab is in front, the one showing Display 1,
        // else the first: FindObjectsOfTypeAll lists them in no particular order, and taking the first one found captured a
        // Game view docked behind another tab (then fell back to the camera, losing the UI) or the one showing Display 2.
        // Only a Game view that is ON SCREEN keeps its target current: docked behind another tab (the usual layout outside
        // Play Mode: Scene in front, Game behind) the target is not there at all — measured null.
        private static RenderTexture? FindGameViewTarget()
        {
            var gameViewType = System.Type.GetType("UnityEditor.GameView,UnityEditor");
            if (gameViewType == null) return null;
            var targetField = FindField(gameViewType, "m_TargetTexture");
            var parentField = typeof(EditorWindow).GetField("m_Parent", BindingFlags.NonPublic | BindingFlags.Instance);
            if (targetField == null || parentField == null) return null;

            Object? chosen = null;
            object? chosenHost = null;
            foreach (var view in Resources.FindObjectsOfTypeAll(gameViewType))
            {
                var host = parentField.GetValue(view);
                if (host == null || !IsFrontTab(host, view)) continue;
                if (chosen == null || (TargetDisplay(view) == 0 && TargetDisplay(chosen) != 0))
                {
                    chosen = view;
                    chosenHost = host;
                }
            }
            if (chosen == null || chosenHost == null) return null;

            // Outside Play Mode the Game view renders on repaint only: make it draw the current scene first, as the
            // SceneView path does. In Play Mode the player loop has already rendered the frame.
            if (!Application.isPlaying && !TryRepaintImmediately(chosenHost))
                ((EditorWindow)chosen).Repaint();

            return targetField.GetValue(chosen) is RenderTexture target && target.IsCreated() ? target : null;
        }

        // The display a Game view shows (0 = Display 1), read from PlayModeView's internal targetDisplay; 0 when not found.
        private static int TargetDisplay(Object view)
        {
            for (var t = view.GetType(); t != null; t = t.BaseType)
            {
                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                var property = t.GetProperty("targetDisplay", flags);
                if (property != null && property.PropertyType == typeof(int) && property.GetIndexParameters().Length == 0)
                    return (int)property.GetValue(view)!;
                var field = t.GetField("m_TargetDisplay", flags);
                if (field != null && field.FieldType == typeof(int))
                    return (int)field.GetValue(view)!;
            }
            return 0;
        }

        // A non-public instance field declared anywhere up the type's base chain.
        private static FieldInfo? FindField(System.Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            return null;
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

        // How much of the picture is one colour: the share of a 16×16 grid of samples within a few levels of the median
        // colour. 1 for a flat fill, well under that for any scene with something in it. 256 GetPixel calls on the CPU copy
        // ReadPixels already made — no second pass over the whole image.
        private static float Flatness(Texture2D tex)
        {
            const int grid = 16, tolerance = 12;
            const int n = grid * grid;
            var r = new int[n];
            var g = new int[n];
            var b = new int[n];
            for (int i = 0; i < n; i++)
            {
                int x = ((i % grid) * 2 + 1) * tex.width / (grid * 2);
                int y = ((i / grid) * 2 + 1) * tex.height / (grid * 2);
                Color32 c = tex.GetPixel(x, y);
                r[i] = c.r; g[i] = c.g; b[i] = c.b;
            }
            int mr = Median(r), mg = Median(g), mb = Median(b);
            int near = 0;
            for (int i = 0; i < n; i++)
                if (Mathf.Abs(r[i] - mr) <= tolerance && Mathf.Abs(g[i] - mg) <= tolerance && Mathf.Abs(b[i] - mb) <= tolerance)
                    near++;
            return near / (float)n;
        }

        private static int Median(int[] values)
        {
            var sorted = (int[])values.Clone();
            System.Array.Sort(sorted);
            return sorted[sorted.Length / 2];
        }

        // Fallback: render the main camera directly. Misses screen-space UI, and see CaptureGameView for why it can
        // disagree with the Game view when GPU occlusion culling is on.
        private static byte[]? CaptureGameViewViaCamera(out float flatness)
        {
            flatness = 0f;
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
                flatness = Flatness(tex);

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
            // RepaintImmediately is internal, so reflect for it; fall back to the queued path. Inside the try: an internal
            // call that throws degrades to the camera render instead of failing the screenshot.
            RenderTexture? rt = null;
            Texture2D? tex = null;
            var prevActive = RenderTexture.active;
            try
            {
                if (!TryRepaintImmediately(guiView))
                    sv.Repaint();

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
