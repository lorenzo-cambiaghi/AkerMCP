#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AkerMcp.Shared.Abstraction;
using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.IO;
using Stride.Core.Presentation.Services;

namespace AkerMcp.StrideAdapter
{
    /// <summary>
    /// Scene create/open/save for Stride Game Studio. Unlike Unity/Godot (file-on-disk
    /// scenes), Stride scenes are package-managed assets, so these go through the editor
    /// SessionViewModel: create a <see cref="SceneAsset"/> via the package, open/save via
    /// the editor services. Runs on the WPF editor thread (dispatched by the IPC handler).
    ///
    /// NOTE: build-verified but validate live in Game Studio — this uses editor internals.
    /// Stride is inherently 3D, so the <c>twoD</c> hint is informational only.
    /// </summary>
    public class StrideSceneManager : ISceneManager
    {
        public SceneResult NewScene(bool twoD, string? savePath)
        {
            var (session, package) = RequireProject();

            var dir = package.AssetMountPoint;
            var leaf = string.IsNullOrEmpty(savePath)
                ? "NewScene"
                : new UFile(savePath!).GetFileNameWithoutExtension();
            var name = UniqueName(dir, string.IsNullOrEmpty(leaf) ? "NewScene" : leaf!);
            var location = UFile.Combine(dir.Path, name);

            var asset = new SceneAsset();
            var assetItem = new AssetItem(location, asset);

            var undo = session.ServiceProvider.Get<IUndoRedoService>();
            AssetViewModel avm;
            using (var tx = undo.CreateTransaction())
            {
                avm = package.CreateAsset(dir, assetItem, true, null);
                undo.SetName(tx, $"Create scene {name}");
            }

            // Open the new scene in an editor window (async; let it run on the UI loop).
            _ = session.ServiceProvider.Get<IAssetEditorsManager>().OpenAssetEditorWindow(avm);

            return new SceneResult
            {
                ScenePath = avm.Url,
                Message = $"Created scene '{avm.Url}' and opening it in Game Studio." +
                          (twoD ? " (Stride is 3D; the 2D hint is informational.)" : "")
            };
        }

        public SceneResult OpenScene(string path)
        {
            var (session, _) = RequireProject();
            var avm = FindScene(session, path);
            if (avm == null)
                return new SceneResult { Message = $"Scene '{path}' not found in the session." };

            _ = session.ServiceProvider.Get<IAssetEditorsManager>().OpenAssetEditorWindow(avm);
            return new SceneResult { ScenePath = avm.Url, Message = $"Opening scene '{avm.Url}'." };
        }

        public SceneResult SaveScene(string? path)
        {
            var (session, _) = RequireProject();
            // Stride saves the whole session (all dirty assets) in place; per-asset save
            // isn't exposed here, so 'path' is ignored.
            _ = session.SaveSession();
            return new SceneResult { Message = "Save requested (session save in progress)." };
        }

        private static (SessionViewModel session, PackageViewModel package) RequireProject()
        {
            var session = StrideSceneBridge.Session
                ?? throw new InvalidOperationException("No active Stride session.");
            PackageViewModel? package = session.CurrentProject
                ?? session.LocalPackages.FirstOrDefault(p => p.Package != null && !p.Package.IsReadOnly);
            if (package == null)
                throw new InvalidOperationException("No editable package/project in the session.");
            return (session, package);
        }

        private static AssetViewModel? FindScene(SessionViewModel session, string path)
        {
            var norm = path.Replace('\\', '/').TrimStart('/');
            if (norm.EndsWith(".sdscene", StringComparison.OrdinalIgnoreCase))
                norm = norm.Substring(0, norm.Length - ".sdscene".Length);

            return session.AllAssets.FirstOrDefault(a =>
                       string.Equals(a.Url, norm, StringComparison.OrdinalIgnoreCase))
                   ?? session.AllAssets.FirstOrDefault(a =>
                       a.Asset is SceneAsset &&
                       a.Url.EndsWith(norm, StringComparison.OrdinalIgnoreCase));
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
    }
}
