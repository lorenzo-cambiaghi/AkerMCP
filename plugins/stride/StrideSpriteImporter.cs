#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AkerMcp.Shared.Abstraction;
using Stride.Assets.Sprite;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Core.Presentation.Services;
using Stride.Core.Serialization;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering.Sprites;

namespace AkerMcp.StrideAdapter
{
    /// <summary>
    /// Imports a server-rasterized PNG into Stride Game Studio as a sprite:
    ///   1. writes the PNG under the project's Resources/Placeholders folder and creates a
    ///      <see cref="SpriteSheetAsset"/> (.sdsheet) with one sprite over the whole image,
    ///      honouring pixels-per-unit and pivot;
    ///   2. when placement is asked for (scene_path or a position), adds an entity with a
    ///      <see cref="SpriteComponent"/> showing that sheet to the edited scene asset,
    ///      through the same undoable hierarchy graph as `create`, so it is saved with the
    ///      scene and survives a reload.
    ///
    /// The previous version dropped a runtime-only entity into the editor game's root scene
    /// (lost on reload, never in the scene asset), loaded a GPU texture that was never
    /// disposed, and blocked the WPF thread up to 10 s waiting for the game thread. The
    /// placed entity now references the asset, and the editor game shows it once the sheet
    /// is compiled, like any sprite dropped from the asset view.
    ///
    /// Runs on the WPF editor thread (ImportSprite is dispatched there). Any failure throws,
    /// so the tool reports an error instead of an "Ok" with a failure in its message.
    ///
    /// NOTE: build-verified only; validate live in Game Studio (uses editor internals).
    /// </summary>
    public class StrideSpriteImporter : ISpriteImporter
    {
        public SpriteImportResult ImportSprite(SpriteImportRequest req)
        {
            var session = StrideSceneBridge.Session
                ?? throw new InvalidOperationException("No active Stride session.");
            PackageViewModel package = session.CurrentProject
                ?? session.LocalPackages.FirstOrDefault(p => p.Package != null && !p.Package.IsReadOnly)
                ?? throw new InvalidOperationException("No editable package/project in the session.");

            var (width, height) = PngSize(req.Png);

            var pkgOsPath = package.Package.FullPath?.ToOSPath();
            var projectDir = !string.IsNullOrEmpty(pkgOsPath)
                ? Path.GetDirectoryName(pkgOsPath)!
                : Path.GetTempPath();
            var resDir = Path.Combine(projectDir, "Resources", "Placeholders");

            // The name first, then the file: picking the file name from the raw request
            // name made a second import of "hero" overwrite the PNG the first "hero" sheet
            // still points at. Unique as an asset AND as a file on disk.
            var dir = package.AssetMountPoint;
            var name = UniqueName(dir, Sanitize(req.Name), resDir);
            Directory.CreateDirectory(resDir);
            var pngPath = Path.Combine(resDir, name + ".png");
            File.WriteAllBytes(pngPath, req.Png);

            var sheet = new SpriteSheetAsset { Type = SpriteSheetType.Sprite2D };
            sheet.Sprites.Add(new SpriteInfo
            {
                Source = new UFile(pngPath),
                Name = name,
                // A zero-size region is skipped by the sheet compiler: the sprite would
                // have no texture.
                TextureRegion = new Rectangle(0, 0, width, height),
                PixelsPerUnit = req.PixelsPerUnit > 0 ? req.PixelsPerUnit : 100f,
                // The request's pivot is 0..1 from the bottom-left; Stride's Center is in
                // pixels from the top-left of the region (CenterFromMiddle off).
                Center = new Vector2(width * Clamp01(req.PivotX), height * (1f - Clamp01(req.PivotY))),
                CenterFromMiddle = false,
            });

            var undo = session.ServiceProvider.Get<IUndoRedoService>();
            AssetViewModel avm;
            using (var tx = undo.CreateTransaction())
            {
                avm = package.CreateAsset(dir, new AssetItem(UFile.Combine(dir.Path, name), sheet), true, null);
                undo.SetName(tx, $"Create sprite sheet {name}");
            }

            // Surface the new asset in the UI — without NotifyAssetPropertiesChanged the
            // asset exists in the session model but the asset view doesn't refresh, so it
            // looks like nothing was created.
            Diag($"sprite sheet created: url='{avm.Url}', source='{pngPath}', pkg='{package.Name}'");
            try { session.NotifyAssetPropertiesChanged(new[] { avm }); }
            catch (Exception ex) { Diag("NotifyAssetPropertiesChanged failed: " + ex.Message); }
            try { session.ActiveAssetView?.SelectAssets(new[] { avm }); }
            catch (Exception ex) { Diag("SelectAssets failed: " + ex.Message); }

            bool place = !string.IsNullOrEmpty(req.ScenePath) || req.PosX.HasValue || req.PosY.HasValue || req.PosZ.HasValue;
            if (!place)
            {
                return new SpriteImportResult
                {
                    AssetPath = avm.Url,
                    Message = $"Imported sprite sheet '{avm.Url}' ({width}x{height}, source {pngPath}). " +
                              "Not placed: pass scene_path or position to add a sprite entity to the open scene."
                };
            }

            string nodePath;
            try
            {
                nodePath = PlaceInScene(session, avm, name, req);
            }
            catch (Exception ex)
            {
                // The asset exists: say so, so a retry places it instead of importing a duplicate.
                throw new InvalidOperationException(
                    $"Imported sprite sheet '{avm.Url}', but placing it in the scene failed: {ex.Message}", ex);
            }

            return new SpriteImportResult
            {
                AssetPath = avm.Url,
                NodePath = nodePath,
                Message = $"Imported sprite sheet '{avm.Url}' ({width}x{height}) and placed it at {nodePath}. " +
                          "The Scene view shows it once Game Studio has compiled the sheet."
            };
        }

        // The entity the asset view's drag-and-drop would build (SpriteFromSheet over the
        // sheet, see Game Studio's AddSpriteSheetAssetPolicy), added to the scene asset.
        private static string PlaceInScene(SessionViewModel session, AssetViewModel sheet, string name, SpriteImportRequest req)
        {
            Guid? parentId = null;
            var parentPath = req.ScenePath?.Trim();
            if (!string.IsNullOrEmpty(parentPath) && parentPath.Trim('/').Length > 0)
            {
                var parent = new StrideSceneGraph(session).GetNode(parentPath)?.UnderlyingObject as Entity
                    ?? throw new InvalidOperationException($"Parent '{parentPath}' not found in the open scene.");
                parentId = parent.Id;
            }

            var entity = new Entity(name)
            {
                new SpriteComponent
                {
                    // A reference to the asset (id + url), resolved when the scene is
                    // compiled, the same object the editor puts in a reference field.
                    SpriteProvider = new SpriteFromSheet
                    {
                        Sheet = AttachedReferenceManager.CreateProxyObject<SpriteSheet>(sheet.AssetItem.Id, sheet.Url)
                    },
                    Sampler = string.Equals(req.Filter, "point", StringComparison.OrdinalIgnoreCase)
                        ? SpriteSampler.PointClamp
                        : SpriteSampler.LinearClamp,
                }
            };
            entity.Transform.Position = new Vector3(req.PosX ?? 0f, req.PosY ?? 0f, req.PosZ ?? 0f);

            StrideSceneBridge.AddEntity(entity, parentId, $"Place sprite {name}");
            return new StrideSceneNode(entity).Path;
        }

        private static string UniqueName(DirectoryBaseViewModel dir, string baseName, string resDir)
        {
            var existing = new HashSet<string>(dir.Assets.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
            bool Taken(string n) => existing.Contains(n) || File.Exists(Path.Combine(resDir, n + ".png"));

            if (!Taken(baseName)) return baseName;
            for (int i = 1; ; i++)
            {
                var n = baseName + "_" + i;
                if (!Taken(n)) return n;
            }
        }

        // Width and height from the IHDR chunk, which the PNG format puts first.
        private static (int width, int height) PngSize(byte[] png)
        {
            if (png.Length < 24 || png[0] != 0x89 || png[1] != (byte)'P' || png[2] != (byte)'N' || png[3] != (byte)'G'
                || png[12] != (byte)'I' || png[13] != (byte)'H' || png[14] != (byte)'D' || png[15] != (byte)'R')
                throw new InvalidOperationException("The sprite image is not a PNG.");
            int width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            int height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException($"The sprite image has an invalid size ({width}x{height}).");
            return (width, height);
        }

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "sprite" : name;
        }

        private static void Diag(string msg) => StrideDiag.Log("sprite-importer", msg);
    }
}
