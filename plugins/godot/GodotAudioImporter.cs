#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.GodotAdapter
{
    /// <summary>
    /// Imports a server-synthesized WAV as a Godot AudioStreamWav and optionally places an
    /// audio player in the edited scene. Runs on the main thread (dispatched by the IPC
    /// handler). The WAV goes through the editor's importer, like GodotSpriteImporter's PNG,
    /// so the scene references the file instead of carrying the samples.
    /// </summary>
    public class GodotAudioImporter : ISoundImporter
    {
        private const string ResDir = "res://placeholders";

        // ResourceImporterWAV's edit/loop_mode: 0 Detect From WAV, 1 Disabled, 2 Forward,
        // 3 Ping-Pong, 4 Backward. Our WAV carries no smpl chunk, so "detect" means no loop.
        private const int LoopModeDetect = 0;
        private const int LoopModeForward = 2;

        public SoundImportResult ImportSound(SoundImportRequest req)
        {
            var safeName = Sanitize(req.Name);
            var resPath = $"{ResDir}/{safeName}.wav";

            bool hasPosition = req.PosX.HasValue || req.PosY.HasValue || req.PosZ.HasValue;
            bool place = !string.IsNullOrEmpty(req.ScenePath) || hasPosition || req.AutoPlay;
            // Resolved before anything is written; see GodotSpriteImporter for why not GetNodeOrNull.
            var parent = place ? GodotSpriteImporter.ResolveParent(req.ScenePath) : null;

            Directory.CreateDirectory(ProjectSettings.GlobalizePath(ResDir));
            File.WriteAllBytes(ProjectSettings.GlobalizePath(resPath), req.Wav);

            // Looping belongs to the stream, and an imported stream takes it from its import
            // options: set on the loaded instance it would be lost on the next editor start.
            var importParams = new Dictionary<string, Variant>
            {
                ["edit/loop_mode"] = req.Loop ? LoopModeForward : LoopModeDetect,
                ["edit/loop_begin"] = 0,
                ["edit/loop_end"] = -1,
            };

            string? note = place && parent == null ? " No scene is open in the editor, so no player was placed." : null;
            AudioStream? stream = GodotResourceImport.ImportAndLoad<AudioStream>(resPath, importParams);
            if (stream == null && parent != null)
            {
                // The editor could not import it now (a full scan is running): place a stream built
                // from the samples, which the scene embeds, rather than nothing.
                stream = BuildStream(req.Wav, req.Loop);
                note = " The editor was busy scanning, so the sound is embedded in the scene for now; " +
                       "run create_sound again once the scan ends to reference the file instead.";
            }
            else if (stream == null)
            {
                note += " The editor imports the file on its next filesystem scan.";
            }

            string? nodePath = null;
            Node? player = null;
            if (parent != null && stream != null)
            {
                player = CreatePlayer(req, parent, hasPosition, stream);
                player.Name = safeName;

                GodotSceneGraph.AddChildUndoable(parent, player, $"AkerMcp: Place sound {safeName}");
                nodePath = new GodotSceneNode(player).Path;
            }

            return new SoundImportResult
            {
                AssetPath = resPath,
                NodePath = nodePath,
                Message = (nodePath != null && player != null
                    ? $"Imported sound '{safeName}' and placed an {player.GetClass()} at {nodePath}."
                    : $"Imported sound '{safeName}' at {resPath}.") + note
            };
        }

        // The player type follows what the sound is for. It was always an AudioStreamPlayer2D, so
        // music and UI sounds were panned and attenuated by the 2D camera, and in a 3D scene the
        // player sat outside the 3D world entirely.
        //   no position            → AudioStreamPlayer   (non-positional: music, UI)
        //   z given, or a 3D place → AudioStreamPlayer3D
        //   otherwise              → AudioStreamPlayer2D
        private static Node CreatePlayer(SoundImportRequest req, Node parent, bool hasPosition, AudioStream stream)
        {
            float volumeDb = LinearToDb(req.Volume);
            if (!hasPosition)
                return new AudioStreamPlayer { Stream = stream, VolumeDb = volumeDb, Autoplay = req.AutoPlay };

            if (req.PosZ.HasValue || Is3DPlace(parent))
                return new AudioStreamPlayer3D
                {
                    Stream = stream, VolumeDb = volumeDb, Autoplay = req.AutoPlay,
                    Position = new Vector3(req.PosX ?? 0f, req.PosY ?? 0f, req.PosZ ?? 0f),
                };

            return new AudioStreamPlayer2D
            {
                Stream = stream, VolumeDb = volumeDb, Autoplay = req.AutoPlay,
                Position = new Vector2(req.PosX ?? 0f, req.PosY ?? 0f),
            };
        }

        // The parent decides first (a positional player is positioned relative to it); a plain
        // Node parent defers to the scene root.
        private static bool Is3DPlace(Node parent)
        {
            if (parent is Node3D) return true;
            if (parent is CanvasItem) return false;
            return EditorInterface.Singleton.GetEditedSceneRoot() is Node3D;
        }

        // Fallback only (see ImportSound): parse our own 44-byte PCM WAV header and build an
        // AudioStreamWav from the samples.
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
#endif
