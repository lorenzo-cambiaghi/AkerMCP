#if TOOLS
#nullable enable
using System;
using System.IO;
using Godot;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.GodotAdapter
{
    /// <summary>
    /// Scene create/open/save for Godot. Runs on the main thread (dispatched by the IPC
    /// handler). A new scene is built as a root node, packed to a .tscn, and opened in
    /// the editor. twoD picks a Node2D root (vs Node3D). Open and save go through the
    /// editor, never around it.
    /// </summary>
    public class GodotSceneManager : ISceneManager
    {
        public SceneResult NewScene(bool twoD, string? savePath)
        {
            var resPath = string.IsNullOrEmpty(savePath) ? "res://scenes/Untitled.tscn" : NormalizeResPath(savePath!);
            EnsureDir(resPath);

            // Writing the file directly is right here and only here: no editor tab holds this
            // scene yet, so there is no open copy for the file to go out of step with.
            Node root = twoD ? new Node2D() : new Node3D();
            root.Name = "Root";

            var packed = new PackedScene();
            packed.Pack(root);
            var err = ResourceSaver.Save(packed, resPath);
            root.QueueFree();

            if (err != Error.Ok)
                return new SceneResult { Message = $"Failed to create scene: {err}" };

            // So the FileSystem dock shows the file without waiting for the next scan.
            EditorInterface.Singleton.GetResourceFilesystem()?.UpdateFile(resPath);
            EditorInterface.Singleton.OpenSceneFromPath(resPath);
            return new SceneResult
            {
                ScenePath = resPath,
                Message = $"Created a new {(twoD ? "2D" : "3D")} scene at {resPath}."
            };
        }

        public SceneResult OpenScene(string path)
        {
            var resPath = NormalizeResPath(path);

            // OpenSceneFromPath reports a bad path only in the editor's own Output panel and returns
            // nothing, so a typo used to come back as "Opened scene …" with the old scene still open.
            if (!ResourceLoader.Exists(resPath) && !Godot.FileAccess.FileExists(resPath))
                throw new FileNotFoundException($"Scene not found: '{resPath}'.");

            EditorInterface.Singleton.OpenSceneFromPath(resPath);

            var opened = EditorInterface.Singleton.GetEditedSceneRoot()?.SceneFilePath;
            if (!string.Equals(opened, resPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"The editor did not open '{resPath}' (the edited scene is '{(string.IsNullOrEmpty(opened) ? "none" : opened)}'). " +
                    "It may not be a scene, or it has broken dependencies: see the editor's Output panel.");

            return new SceneResult { ScenePath = resPath, Message = $"Opened scene {resPath}." };
        }

        public SceneResult SaveScene(string? path)
        {
            var editor = EditorInterface.Singleton;
            var root = editor.GetEditedSceneRoot();
            if (root == null)
                return new SceneResult { Message = "No edited scene to save." };

            var current = root.SceneFilePath;
            var target = string.IsNullOrEmpty(path) ? current : NormalizeResPath(path!);
            if (string.IsNullOrEmpty(target))
                return new SceneResult { Message = "Scene has no path yet; provide 'path' to save it." };

            // The save goes through the editor. PackedScene.Pack + ResourceSaver.Save wrote the file
            // behind its back: the open tab kept believing its own copy was the newest, so the next
            // focus-in asked to Reload or Resave, and the unsaved marker never cleared.
            if (string.Equals(target, current, StringComparison.OrdinalIgnoreCase))
            {
                var err = editor.SaveScene();
                return new SceneResult
                {
                    ScenePath = target,
                    Message = err == Error.Ok ? $"Saved scene to {target}." : $"Scene save failed: {err}"
                };
            }

            EnsureDir(target);
            var file = ProjectSettings.GlobalizePath(target);
            var before = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;

            // Save As: the edited scene now lives at the new path, as with the editor's own menu.
            // It returns nothing, so the file on disk is the only evidence that it worked.
            editor.SaveSceneAs(target);

            bool written = File.Exists(file) && File.GetLastWriteTimeUtc(file) != before;
            return new SceneResult
            {
                ScenePath = target,
                Message = written ? $"Saved scene to {target}." : $"Scene save failed: {target} was not written."
            };
        }

        /// <summary>
        /// Takes a path the way a caller is likely to write it ("scenes/Main.tscn", "/scenes/Main.tscn",
        /// an absolute path inside the project, a uid://) and returns the res:// path Godot wants.
        /// </summary>
        internal static string NormalizeResPath(string path)
        {
            path = path.Trim().Replace('\\', '/');
            if (path.StartsWith("res://", StringComparison.Ordinal) || path.StartsWith("user://", StringComparison.Ordinal))
                return path;

            if (path.StartsWith("uid://", StringComparison.Ordinal))
            {
                var id = ResourceUid.TextToId(path);
                return ResourceUid.HasId(id) ? ResourceUid.GetIdPath(id) : path;
            }

            if (Path.IsPathFullyQualified(path))
            {
                var local = ProjectSettings.LocalizePath(path);
                if (local.StartsWith("res://", StringComparison.Ordinal)) return local;
            }

            return "res://" + path.TrimStart('/');
        }

        private static void EnsureDir(string resPath)
        {
            // GetBaseDir, not the text before the last '/': for "res://Main.tscn" that was "res:/",
            // which is no directory at all.
            var dir = resPath.GetBaseDir();
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(ProjectSettings.GlobalizePath(dir));
        }
    }
}
#endif
