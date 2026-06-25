#nullable enable
using System.IO;
using Godot;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.GodotAdapter
{
    /// <summary>
    /// Scene create/open/save for Godot. Runs on the main thread (dispatched by the IPC
    /// handler). A new scene is built as a root node, packed to a .tscn, and opened in
    /// the editor. twoD picks a Node2D root (vs Node3D).
    /// </summary>
    public class GodotSceneManager : ISceneManager
    {
        public SceneResult NewScene(bool twoD, string? savePath)
        {
            var resPath = string.IsNullOrEmpty(savePath) ? "res://scenes/Untitled.tscn" : savePath!;
            EnsureDir(resPath);

            Node root = twoD ? new Node2D() : new Node3D();
            root.Name = "Root";

            var packed = new PackedScene();
            packed.Pack(root);
            var err = ResourceSaver.Save(packed, resPath);
            root.QueueFree();

            if (err != Error.Ok)
                return new SceneResult { Message = $"Failed to create scene: {err}" };

            EditorInterface.Singleton.OpenSceneFromPath(resPath);
            return new SceneResult
            {
                ScenePath = resPath,
                Message = $"Created a new {(twoD ? "2D" : "3D")} scene at {resPath}."
            };
        }

        public SceneResult OpenScene(string path)
        {
            EditorInterface.Singleton.OpenSceneFromPath(path);
            return new SceneResult { ScenePath = path, Message = $"Opened scene {path}." };
        }

        public SceneResult SaveScene(string? path)
        {
            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
                return new SceneResult { Message = "No edited scene to save." };

            var target = string.IsNullOrEmpty(path) ? root.SceneFilePath : path!;
            if (string.IsNullOrEmpty(target))
                return new SceneResult { Message = "Scene has no path yet; provide 'path' to save it." };

            EnsureDir(target);
            var packed = new PackedScene();
            packed.Pack(root);
            var err = ResourceSaver.Save(packed, target);
            return new SceneResult
            {
                ScenePath = target,
                Message = err == Error.Ok ? $"Saved scene to {target}." : $"Scene save failed: {err}"
            };
        }

        private static void EnsureDir(string resPath)
        {
            var slash = resPath.LastIndexOf('/');
            if (slash <= 0) return;
            Directory.CreateDirectory(ProjectSettings.GlobalizePath(resPath.Substring(0, slash)));
        }
    }
}
