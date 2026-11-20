#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
    /// Opening an editor and saving the session are asynchronous in Game Studio. They used
    /// to be fired and forgotten, so a failed open or save still answered "Opening..." /
    /// "Save requested". Now each is awaited (pumping the dispatcher, see
    /// <see cref="StrideUiWait"/>) up to a timeout: a failure is an error, and a call that
    /// is still running when the timeout ends says so, and logs its outcome when it comes.
    ///
    /// NOTE: build-verified but validate live in Game Studio — this uses editor internals.
    /// Stride is inherently 3D, so the <c>twoD</c> hint is informational only.
    /// </summary>
    public class StrideSceneManager : ISceneManager
    {
        // Loading a large scene into its editor, or saving a big session, can take a
        // while; past these the tool answers and the outcome goes to the log.
        private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(60);

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

            // The asset exists from here on: a failed open must not hide that.
            string opened;
            try
            {
                opened = Open(session, avm)
                    ? $"Created scene '{avm.Url}' and opened it in Game Studio."
                    : $"Created scene '{avm.Url}'; its editor is still opening (check get_console_logs if it does not appear).";
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Created scene '{avm.Url}', but opening it failed: {ex.Message}", ex);
            }

            return new SceneResult
            {
                ScenePath = avm.Url,
                Message = opened + (twoD ? " (Stride is 3D; the 2D hint is informational.)" : "")
            };
        }

        public SceneResult OpenScene(string path)
        {
            var (session, _) = RequireProject();
            var avm = FindScene(session, path)
                ?? throw new InvalidOperationException($"Scene '{path}' not found in the session.");

            return new SceneResult
            {
                ScenePath = avm.Url,
                Message = Open(session, avm)
                    ? $"Opened scene '{avm.Url}'."
                    : $"Scene '{avm.Url}' is still opening (check get_console_logs if it does not appear)."
            };
        }

        public SceneResult SaveScene(string? path)
        {
            var (session, _) = RequireProject();
            // Stride saves the whole session (all dirty assets) in place; per-asset save
            // isn't exposed here, so 'path' is ignored.
            var save = session.SaveSession();
            if (!StrideUiWait.Wait(save, SaveTimeout))
            {
                save.ContinueWith(t =>
                {
                    if (t.IsFaulted) StrideDiag.Error("scene-manager", "Session save failed: " + t.Exception?.GetBaseException().Message);
                    else if (!t.Result) StrideDiag.Error("scene-manager", "Game Studio reported that the session save failed.");
                }, TaskScheduler.Default);
                return new SceneResult { Message = "Save still in progress; its outcome will be in get_console_logs if it fails." };
            }

            if (!save.GetAwaiter().GetResult())
                throw new InvalidOperationException("Game Studio reported that the session save failed (see its output).");
            return new SceneResult { Message = "Session saved (all modified assets, scenes included)." };
        }

        // True once the editor is open, false if it is still opening at the timeout;
        // throws if opening failed.
        private static bool Open(SessionViewModel session, AssetViewModel avm)
        {
            Task open = session.ServiceProvider.Get<IAssetEditorsManager>().OpenAssetEditorWindow(avm);
            if (!StrideUiWait.Wait(open, OpenTimeout))
            {
                var url = avm.Url;
                open.ContinueWith(t => StrideDiag.Error("scene-manager", $"Opening '{url}' failed: {t.Exception?.GetBaseException().Message}"),
                    TaskContinuationOptions.OnlyOnFaulted);
                return false;
            }
            open.GetAwaiter().GetResult();   // rethrows the failure, if any
            return true;
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

            // Scenes only, in both passes: an exact url match on another asset type would
            // open some other editor and report it as the scene.
            return session.AllAssets.FirstOrDefault(a =>
                       a.Asset is SceneAsset &&
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
