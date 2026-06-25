#nullable enable
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
    /// </summary>
    public class UnitySceneManager : ISceneManager
    {
        public SceneResult NewScene(bool twoD, string? savePath)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneSetupMode.Single);

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
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            return new SceneResult { ScenePath = scene.path, Message = $"Opened scene {scene.path}." };
        }

        public SceneResult SaveScene(string? path)
        {
            var scene = EditorSceneManager.GetActiveScene();
            bool ok;
            string target;
            if (string.IsNullOrEmpty(path))
            {
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

        // Creates the nested Asset folders for a scene path like "Assets/Scenes/Sub/X.unity".
        private static void EnsureAssetFolder(string assetPath)
        {
            var dir = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(dir) || AssetDatabase.IsValidFolder(dir)) return;

            var parts = dir.Split('/');
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
