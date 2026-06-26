#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AkerMcp.Shared.Abstraction;
using Stride.Assets.Textures;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Core.Presentation.Services;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering.Sprites;

namespace AkerMcp.StrideAdapter
{
    /// <summary>
    /// Imports a server-rasterized PNG into Stride Game Studio:
    ///   1. Persists it as a real <see cref="TextureAsset"/> (.sdtex) in the project package
    ///      (the PNG source is written under the project's Resources folder) — survives reload.
    ///   2. Also drops a runtime Sprite entity into the editor preview scene for immediate
    ///      visibility in the Scene view / screenshots.
    /// The two steps are independent (one failing doesn't abort the other).
    ///
    /// The asset-side step runs on the WPF editor thread (where ImportSprite is dispatched);
    /// the runtime step is marshalled to the game thread (like StrideScreenCapture).
    ///
    /// NOTE: build-verified but validate live in Game Studio — uses editor internals.
    /// </summary>
    public class StrideSpriteImporter : ISpriteImporter
    {
        public SpriteImportResult ImportSprite(SpriteImportRequest req)
        {
            var safeName = Sanitize(req.Name);

            string assetPath = "(not persisted)";
            string persistMsg;
            try
            {
                assetPath = PersistTextureAsset(req, safeName);
                persistMsg = $"persisted as asset '{assetPath}'";
            }
            catch (Exception ex)
            {
                persistMsg = "asset persistence failed: " + ex.Message;
                Diag(persistMsg);
            }

            string? nodePath = null;
            string runtimeMsg;
            try
            {
                nodePath = AddRuntimePreview(req, safeName);
                runtimeMsg = nodePath != null ? $"runtime preview entity '{nodePath}'" : "no active preview game";
            }
            catch (Exception ex)
            {
                runtimeMsg = "runtime preview failed: " + ex.Message;
                Diag(runtimeMsg);
            }

            return new SpriteImportResult
            {
                AssetPath = assetPath,
                NodePath = nodePath,
                Message = $"Stride sprite '{safeName}': {persistMsg}; {runtimeMsg}."
            };
        }

        // --- asset-side persistence (.sdtex) ------------------------------------

        private static string PersistTextureAsset(SpriteImportRequest req, string safeName)
        {
            var session = StrideSceneBridge.Session
                ?? throw new InvalidOperationException("No active Stride session.");
            PackageViewModel? package = session.CurrentProject
                ?? session.LocalPackages.FirstOrDefault(p => p.Package != null && !p.Package.IsReadOnly);
            if (package == null)
                throw new InvalidOperationException("No editable package/project in the session.");

            // Write the PNG source under the project's Resources folder.
            var pkgOsPath = package.Package.FullPath?.ToOSPath();
            var projectDir = !string.IsNullOrEmpty(pkgOsPath)
                ? Path.GetDirectoryName(pkgOsPath)!
                : Path.GetTempPath();
            var resDir = Path.Combine(projectDir, "Resources", "Placeholders");
            Directory.CreateDirectory(resDir);
            var pngPath = Path.Combine(resDir, safeName + ".png");
            File.WriteAllBytes(pngPath, req.Png);

            var dir = package.AssetMountPoint;
            var name = UniqueName(dir, safeName);
            var location = UFile.Combine(dir.Path, name);

            var texAsset = new TextureAsset { Source = new UFile(pngPath) };
            var assetItem = new AssetItem(location, texAsset);

            var undo = session.ServiceProvider.Get<IUndoRedoService>();
            AssetViewModel avm;
            using (var tx = undo.CreateTransaction())
            {
                avm = package.CreateAsset(dir, assetItem, true, null);
                undo.SetName(tx, $"Create sprite texture {name}");
            }

            // Surface the new asset in the UI — without NotifyAssetPropertiesChanged the
            // asset exists in the session model but the asset view doesn't refresh, so it
            // looks like nothing was created (the scene path got away with it only because
            // OpenAssetEditorWindow forced a refresh).
            Diag($"texture asset created: url='{avm.Url}', dir='{dir.Path}', dirAssets={dir.Assets.Count()}, pkg='{package.Name}'");
            try { session.NotifyAssetPropertiesChanged(new[] { avm }); }
            catch (Exception ex) { Diag("NotifyAssetPropertiesChanged failed: " + ex.Message); }
            try { session.ActiveAssetView?.SelectAssets(new[] { avm }); }
            catch (Exception ex) { Diag("SelectAssets failed: " + ex.Message); }

            return avm.Url;
        }

        private static string UniqueName(DirectoryBaseViewModel dir, string baseName)
        {
            var existing = new HashSet<string>(dir.Assets.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
            if (!existing.Contains(baseName)) return baseName;
            for (int i = 1; ; i++)
            {
                var n = baseName + "_" + i;
                if (!existing.Contains(n)) return n;
            }
        }

        // --- runtime preview (immediate visibility) -----------------------------

        private static string? AddRuntimePreview(SpriteImportRequest req, string safeName)
        {
            var game = StrideSceneBridge.ActiveEditorGame();
            if (game == null) return null;

            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            game.Script.AddTask(async () =>
            {
                try
                {
                    using var ms = new MemoryStream(req.Png);
                    var texture = Texture.Load(game.GraphicsDevice, ms);
                    var provider = new SpriteFromTexture
                    {
                        Texture = texture,
                        PixelsPerUnit = req.PixelsPerUnit,
                        IsTransparent = true,
                    };
                    var entity = new Entity(safeName) { new SpriteComponent { SpriteProvider = provider } };
                    entity.Transform.Position = new Vector3(req.PosX ?? 0f, req.PosY ?? 0f, req.PosZ ?? 0f);
                    game.SceneSystem.SceneInstance.RootScene.Entities.Add(entity);
                    tcs.TrySetResult(entity.Name);
                }
                catch (Exception ex) { tcs.TrySetResult("ERROR:" + ex.Message); }
                await Task.CompletedTask;
            });

            var res = tcs.Task.Wait(10000) ? tcs.Task.Result : null;
            if (res != null && res.StartsWith("ERROR:", StringComparison.Ordinal))
                throw new InvalidOperationException(res.Substring(6));
            return res;
        }

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "sprite" : name;
        }

        private static void Diag(string msg)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "akermcp-stride.log"),
                    $"{DateTime.Now:HH:mm:ss} [sprite-importer] {msg}{Environment.NewLine}");
            }
            catch { /* diagnostics must never throw */ }
        }
    }
}
