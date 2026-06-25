#nullable enable
using System.IO;
using UnityEngine;
using UnityEditor;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.Unity
{
    /// <summary>
    /// Imports a server-rasterized PNG as a Unity Sprite and optionally places it in the
    /// scene. Runs on the main thread (dispatched by IpcRequestHandler), so Editor APIs
    /// are safe to call directly.
    /// </summary>
    public class UnitySpriteImporter : ISpriteImporter
    {
        private const string PlaceholderFolder = "Assets/Placeholders";

        public SpriteImportResult ImportSprite(SpriteImportRequest req)
        {
            if (!AssetDatabase.IsValidFolder(PlaceholderFolder))
                AssetDatabase.CreateFolder("Assets", "Placeholders");

            var safeName = Sanitize(req.Name);
            var assetPath = $"{PlaceholderFolder}/{safeName}.png";
            var fullPath = Path.Combine(Application.dataPath, "Placeholders", safeName + ".png");

            File.WriteAllBytes(fullPath, req.Png);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
                return new SpriteImportResult
                {
                    AssetPath = assetPath,
                    Message = "Imported PNG but could not obtain a TextureImporter to configure the sprite."
                };

            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            settings.textureType = TextureImporterType.Sprite;
            settings.spriteMode = (int)SpriteImportMode.Single;
            settings.spritePixelsPerUnit = req.PixelsPerUnit;
            settings.spriteAlignment = (int)SpriteAlignment.Custom;
            settings.spritePivot = new Vector2(req.PivotX, req.PivotY);
            settings.filterMode = req.Filter == "point" ? FilterMode.Point : FilterMode.Bilinear;
            settings.alphaIsTransparency = true;
            settings.mipmapEnabled = false;
            settings.wrapMode = TextureWrapMode.Clamp;
            importer.SetTextureSettings(settings);
            importer.SaveAndReimport();

            string? nodePath = null;
            bool place = !string.IsNullOrEmpty(req.ScenePath) || req.PosX.HasValue || req.PosY.HasValue;
            if (place)
            {
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                var go = new GameObject(safeName);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = sprite;

                if (!string.IsNullOrEmpty(req.ScenePath))
                {
                    var parent = GameObject.Find(req.ScenePath);
                    if (parent != null) go.transform.SetParent(parent.transform, false);
                }
                go.transform.position = new Vector3(req.PosX ?? 0f, req.PosY ?? 0f, req.PosZ ?? 0f);
                Undo.RegisterCreatedObjectUndo(go, "Create Sprite Placeholder");
                nodePath = GetScenePath(go.transform);
            }

            return new SpriteImportResult
            {
                AssetPath = assetPath,
                NodePath = nodePath,
                Message = nodePath != null
                    ? $"Imported sprite '{safeName}' (ppu {req.PixelsPerUnit}) and placed it at {nodePath}."
                    : $"Imported sprite '{safeName}' (ppu {req.PixelsPerUnit}) at {assetPath}."
            };
        }

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "sprite" : name;
        }

        private static string GetScenePath(Transform t)
        {
            var path = "/" + t.name;
            while (t.parent != null) { t = t.parent; path = "/" + t.name + path; }
            return path;
        }
    }
}
