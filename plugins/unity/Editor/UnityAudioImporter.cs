#nullable enable

using System.IO;
using UnityEngine;
using UnityEditor;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.Unity
{
    /// <summary>
    /// Imports a server-synthesized WAV as a Unity AudioClip (Unity auto-imports .wav) and
    /// optionally adds an AudioSource to a scene object. Runs on the main thread (dispatched by
    /// the IPC handler). The audio analog of UnitySpriteImporter.
    /// </summary>
    public class UnityAudioImporter : ISoundImporter
    {
        private const string Dir = "Assets/Placeholders";

        public SoundImportResult ImportSound(SoundImportRequest req)
        {
            var safeName = Sanitize(req.Name);
            Directory.CreateDirectory(Dir);
            var assetPath = $"{Dir}/{safeName}.wav";
            File.WriteAllBytes(assetPath, req.Wav);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);

            string? nodePath = null;
            bool place = !string.IsNullOrEmpty(req.ScenePath) || req.PosX.HasValue || req.PosY.HasValue || req.AutoPlay;
            if (place && clip != null)
            {
                GameObject? host = null;
                if (!string.IsNullOrEmpty(req.ScenePath))
                {
                    var node = new UnitySceneGraph().GetNode(req.ScenePath!);
                    host = node?.UnderlyingObject as GameObject;
                }
                if (host == null)
                {
                    host = new GameObject(safeName);
                    if (req.PosX.HasValue || req.PosY.HasValue || req.PosZ.HasValue)
                        host.transform.position = new Vector3(req.PosX ?? 0f, req.PosY ?? 0f, req.PosZ ?? 0f);
                    Undo.RegisterCreatedObjectUndo(host, "Create Audio Source");
                }

                var src = host.GetComponent<AudioSource>() ?? host.AddComponent<AudioSource>();
                src.clip = clip;
                src.volume = Mathf.Clamp01(req.Volume);
                src.loop = req.Loop;
                src.playOnAwake = req.AutoPlay;
                nodePath = new UnitySceneNode(host).Path;
            }

            return new SoundImportResult
            {
                AssetPath = assetPath,
                NodePath = nodePath,
                Message = nodePath != null
                    ? $"Imported sound '{safeName}' and added an AudioSource at {nodePath}."
                    : $"Imported sound '{safeName}' at {assetPath}."
            };
        }

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "sound" : name;
        }
    }
}
