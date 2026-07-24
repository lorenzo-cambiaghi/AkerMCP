#nullable enable
using System;
using System.IO;
using Godot;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.GodotAdapter
{
    /// <summary>
    /// Imports a server-synthesized WAV as a Godot AudioStreamWav and optionally places an
    /// AudioStreamPlayer2D in the edited scene. Runs on the main thread (dispatched by the IPC
    /// handler). Builds the stream straight from the PCM so placement doesn't wait on the
    /// editor's async (re)import — mirroring GodotSpriteImporter.
    /// </summary>
    public class GodotAudioImporter : ISoundImporter
    {
        private const string ResDir = "res://placeholders";

        public SoundImportResult ImportSound(SoundImportRequest req)
        {
            var safeName = Sanitize(req.Name);
            var resPath = $"{ResDir}/{safeName}.wav";

            var globalDir = ProjectSettings.GlobalizePath(ResDir);
            Directory.CreateDirectory(globalDir);
            File.WriteAllBytes(ProjectSettings.GlobalizePath(resPath), req.Wav);
            EditorInterface.Singleton.GetResourceFilesystem()?.Scan();

            var stream = BuildStream(req.Wav, req.Loop);

            string? nodePath = null;
            bool place = !string.IsNullOrEmpty(req.ScenePath) || req.PosX.HasValue || req.PosY.HasValue || req.AutoPlay;
            if (place && stream != null)
            {
                var root = EditorInterface.Singleton.GetEditedSceneRoot();
                if (root != null)
                {
                    var player = new AudioStreamPlayer2D
                    {
                        Name = safeName,
                        Stream = stream,
                        VolumeDb = LinearToDb(req.Volume),
                        Autoplay = req.AutoPlay,
                        Position = new Vector2(req.PosX ?? 0f, req.PosY ?? 0f),
                    };

                    Node parent = root;
                    if (!string.IsNullOrEmpty(req.ScenePath))
                    {
                        var found = root.GetNodeOrNull(req.ScenePath);
                        if (found != null) parent = found;
                    }
                    parent.AddChild(player);
                    player.Owner = root; // serialize with the scene
                    nodePath = player.GetPath().ToString();
                }
            }

            return new SoundImportResult
            {
                AssetPath = resPath,
                NodePath = nodePath,
                Message = nodePath != null
                    ? $"Imported sound '{safeName}' and placed a player at {nodePath}."
                    : $"Imported sound '{safeName}' at {resPath}."
            };
        }

        // Parse our own 44-byte PCM WAV header and build an AudioStreamWav from the samples.
        private static AudioStreamWav? BuildStream(byte[] wav, bool loop)
        {
            if (wav == null || wav.Length <= 44) return null;
            int mixRate = BitConverter.ToInt32(wav, 24); // sampleRate in the fmt chunk

            var pcm = new byte[wav.Length - 44];
            Array.Copy(wav, 44, pcm, 0, pcm.Length);

            var s = new AudioStreamWav
            {
                Data = pcm,
                Format = AudioStreamWav.FormatEnum.Format16Bits,
                MixRate = mixRate,
                Stereo = false,
            };
            if (loop)
            {
                s.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
                s.LoopBegin = 0;
                s.LoopEnd = pcm.Length / 2; // 16-bit mono sample count
            }
            return s;
        }

        private static float LinearToDb(float linear)
            => linear > 0.0001f ? (float)(20.0 * Math.Log10(linear)) : -80f;

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "sound" : name;
        }
    }
}
