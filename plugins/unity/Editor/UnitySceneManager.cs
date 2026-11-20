#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.Unity
{
    /// <summary>
    /// Scene create/open/save for Unity. Runs on the main thread (dispatched by the IPC
    /// handler). NewScene with twoD sets the Main Camera to orthographic with a sky
    /// background, ready for 2D prototyping.
    /// <para>
    /// Refusals throw: the IPC handler turns the exception into a failed call the caller
    /// cannot mistake for a success, which a SceneResult message would not be.
    /// </para>
    /// </summary>
    public class UnitySceneManager : ISceneManager
    {
        public SceneResult NewScene(bool twoD, string? savePath)
        {
            RefuseInPlayMode("new_scene");
            RefuseIfDirty("new_scene");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            if (twoD)
            {
                var cam = Camera.main;
                if (cam != null)
                {
                    cam.orthographic = true;
                    cam.orthographicSize = 5f;
                    cam.transform.position = new Vector3(0f, 0f, -10f);
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0.53f, 0.81f, 0.92f); // sky blue
                }
            }

            string saved = "";
            if (!string.IsNullOrEmpty(savePath))
            {
                EnsureAssetFolder(savePath!);
                if (EditorSceneManager.SaveScene(scene, savePath)) saved = savePath!;
            }

            return new SceneResult
            {
                ScenePath = saved,
                Message = $"Created a new {(twoD ? "2D" : "3D")} scene" +
                          (saved != "" ? $" and saved it to {saved}." : " (unsaved — pass save_path to persist).")
            };
        }

        public SceneResult OpenScene(string path)
        {
            RefuseInPlayMode("open_scene");
            RefuseIfDirty("open_scene");

            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            return new SceneResult { ScenePath = scene.path, Message = $"Opened scene {scene.path}." };
        }

        public SceneResult SaveScene(string? path)
        {
            RefuseInPlayMode("save_scene");

            var scene = EditorSceneManager.GetActiveScene();
            bool ok;
            string target;
            if (string.IsNullOrEmpty(path))
            {
                // A scene never saved has no path, and SaveScene(scene) then opens the modal "Save Scene" dialog:
                // it blocks the editor's main thread, so this call and every MCP call after it hang until someone
                // clicks in Unity.
                if (string.IsNullOrEmpty(scene.path))
                    throw new InvalidOperationException(
                        "The active scene has never been saved, so it has no path: call save_scene with a path, " +
                        "e.g. \"Assets/Scenes/NewScene.unity\". (Without one Unity would open its modal Save dialog, " +
                        "which blocks the editor.)");
                ok = EditorSceneManager.SaveScene(scene);
                target = scene.path;
            }
            else
            {
                EnsureAssetFolder(path!);
                ok = EditorSceneManager.SaveScene(scene, path);
                target = path!;
            }
            return new SceneResult { ScenePath = target, Message = ok ? $"Saved scene to {target}." : "Scene save failed." };
        }

        // Play Mode scenes are runtime copies: opening or creating one in Single mode throws from inside Unity, and a
        // save would write the play-time state over the edited scene.
        private static void RefuseInPlayMode(string tool)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException(
                    $"{tool} is not available in Play Mode: call exit_play first.");
        }

        // NewScene / OpenScene in Single mode close every open scene, and through the API Unity does not ask: unsaved
        // changes were dropped without a word. Refused instead, naming the scenes, so the caller decides what to keep.
        // There is no "discard" switch: the IPC request carries only the path, and adding one is a protocol change.
        private static void RefuseIfDirty(string tool)
        {
            var dirty = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isDirty) dirty.Add(string.IsNullOrEmpty(s.path) ? "(untitled)" : s.path);
            }
            if (dirty.Count == 0) return;

            throw new InvalidOperationException(
                $"{tool} would discard unsaved changes in: {string.Join(", ", dirty)}. " +
                "Call save_scene first (with a path for an untitled scene), then retry.");
        }

        // Creates the nested Asset folders for a scene path like "Assets/Scenes/Sub/X.unity".
        private static void EnsureAssetFolder(string assetPath)
        {
            var dir = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(dir) || AssetDatabase.IsValidFolder(dir)) return;

            var parts = dir!.Split('/');
            var cur = parts[0]; // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                var next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }
    }
}
