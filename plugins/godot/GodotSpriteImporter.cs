#nullable enable
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

            var globalDir = ProjectSettings.GlobalizePath(ResDir);
            Directory.CreateDirectory(globalDir);
            File.WriteAllBytes(ProjectSettings.GlobalizePath(resPath), req.Png);

            // Let the editor pick up the new file for project-level persistence.
            EditorInterface.Singleton.GetResourceFilesystem()?.Scan();

            // Build a texture for immediate placement straight from the bytes — this does
            // not depend on the editor's async (re)import finishing.
            Texture2D? tex = null;
            if (ResourceLoader.Exists(resPath))
                tex = ResourceLoader.Load<Texture2D>(resPath);
            if (tex == null)
            {
                var image = new Image();
                if (image.LoadPngFromBuffer(req.Png) == Error.Ok)
                    tex = ImageTexture.CreateFromImage(image);
            }

            string? nodePath = null;
            bool place = !string.IsNullOrEmpty(req.ScenePath) || req.PosX.HasValue || req.PosY.HasValue;
            if (place && tex != null)
            {
                var root = EditorInterface.Singleton.GetEditedSceneRoot();
                if (root != null)
                {
                    var sprite = new Sprite2D
                    {
                        Name = safeName,
                        Texture = tex,
                        Centered = true,
                        TextureFilter = req.Filter == "point"
                            ? CanvasItem.TextureFilterEnum.Nearest
                            : CanvasItem.TextureFilterEnum.Linear,
                        Position = new Vector2(req.PosX ?? 0f, req.PosY ?? 0f),
                    };

                    Node parent = root;
                    if (!string.IsNullOrEmpty(req.ScenePath))
                    {
                        var found = root.GetNodeOrNull(req.ScenePath);
                        if (found != null) parent = found;
                    }
                    parent.AddChild(sprite);
                    sprite.Owner = root; // so it serializes when the scene is saved
                    nodePath = sprite.GetPath().ToString();
                }
            }

            return new SpriteImportResult
            {
                AssetPath = resPath,
                NodePath = nodePath,
                Message = nodePath != null
                    ? $"Imported sprite '{safeName}' and placed it at {nodePath}."
                    : $"Imported sprite '{safeName}' at {resPath}."
            };
        }

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "sprite" : name;
        }
    }
}
