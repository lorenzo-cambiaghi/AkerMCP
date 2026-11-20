#if TOOLS
#nullable enable
using System;
using System.IO;
using Godot;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.GodotAdapter
{
    /// <summary>
    /// Imports a server-rasterized PNG as a Godot texture and optionally places a
    /// Sprite2D in the edited scene. Runs on the main thread (dispatched by the IPC
    /// handler), so EditorInterface APIs are safe to call directly.
    ///
    /// Note: Godot 2D is pixel-based, so PixelsPerUnit is not applied (unlike Unity).
    /// Texture filtering is set per-node (CanvasItem.TextureFilter), independent of the
    /// asset's import settings.
    /// </summary>
    public class GodotSpriteImporter : ISpriteImporter
    {
        private const string ResDir = "res://placeholders";

        public SpriteImportResult ImportSprite(SpriteImportRequest req)
        {
            var safeName = Sanitize(req.Name);
            var resPath = $"{ResDir}/{safeName}.png";

            // The parent is resolved before anything is written, so a wrong scene_path fails cleanly.
            // It used to go through root.GetNodeOrNull with the tools' leading "/", which Godot reads
            // as an absolute tree path: it never matched, and the sprite fell back to the root.
            bool place = !string.IsNullOrEmpty(req.ScenePath) || req.PosX.HasValue || req.PosY.HasValue;
            var parent = place ? ResolveParent(req.ScenePath) : null;

            Directory.CreateDirectory(ProjectSettings.GlobalizePath(ResDir));
            File.WriteAllBytes(ProjectSettings.GlobalizePath(resPath), req.Png);

            string? note = place && parent == null ? " No scene is open in the editor, so it was not placed." : null;
            var tex = GodotResourceImport.ImportAndLoad<Texture2D>(resPath);
            if (tex == null && parent != null)
            {
                // The editor could not import it now (a full scan is running): place a texture built
                // from the bytes, which the scene embeds, rather than nothing.
                var image = new Image();
                if (image.LoadPngFromBuffer(req.Png) == Error.Ok)
                    tex = ImageTexture.CreateFromImage(image);
                note = " The editor was busy scanning, so the texture is embedded in the scene for now; " +
                       "run create_sprite again once the scan ends to reference the file instead.";
            }
            else if (tex == null)
            {
                note += " The editor imports the file on its next filesystem scan.";
            }

            string? nodePath = null;
            if (parent != null && tex != null)
            {
                var sprite = new Sprite2D
                {
                    Name = safeName,
                    Texture = tex,
                    TextureFilter = req.Filter == "point"
                        ? CanvasItem.TextureFilterEnum.Nearest
                        : CanvasItem.TextureFilterEnum.Linear,
                    Position = new Vector2(req.PosX ?? 0f, req.PosY ?? 0f),
                };
                ApplyPivot(sprite, tex, req.PivotX, req.PivotY);

                GodotSceneGraph.AddChildUndoable(parent, sprite, $"AkerMcp: Place sprite {safeName}");
                // The scene path every other tool takes, not GetPath()'s "/root/@EditorNode@…/…",
                // which set_property could not resolve.
                nodePath = new GodotSceneNode(sprite).Path;
            }

            return new SpriteImportResult
            {
                AssetPath = resPath,
                NodePath = nodePath,
                Message = (nodePath != null
                    ? $"Imported sprite '{safeName}' and placed it at {nodePath}."
                    : $"Imported sprite '{safeName}' at {resPath}.") + note
            };
        }

        /// <summary>The node to place under: the scene root, or the node at <paramref name="scenePath"/>.
        /// Null when no scene is open (the asset is imported, nothing is placed); an error when the
        /// path does not resolve.</summary>
        internal static Node? ResolveParent(string? scenePath)
        {
            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null) return null;
            if (string.IsNullOrEmpty(scenePath)) return root;
            return GodotSceneGraph.ResolvePath(scenePath)
                ?? throw new InvalidOperationException(
                    $"Parent not found: '{scenePath}'. Paths start at the edited scene root, e.g. '/{root.Name}/Child'.");
        }

        // The pivot comes in the engine-neutral convention (0..1, origin bottom-left, see
        // SpriteImportRequest); Godot's 2D y axis points down. The centre keeps Centered, which
        // reads better in the Inspector; any other pivot draws the texture shifted so that the
        // pivot pixel sits on the node's origin.
        private static void ApplyPivot(Sprite2D sprite, Texture2D tex, float pivotX, float pivotY)
        {
            if (Mathf.IsEqualApprox(pivotX, 0.5f) && Mathf.IsEqualApprox(pivotY, 0.5f))
            {
                sprite.Centered = true;
                return;
            }

            var size = tex.GetSize();
            sprite.Centered = false;
            sprite.Offset = new Vector2(-pivotX * size.X, -(1f - pivotY) * size.Y);
        }

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "sprite" : name;
        }
    }
}
#endif
