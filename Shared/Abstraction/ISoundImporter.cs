using System;

namespace AkerMcp.Shared.Abstraction
{
    /// <summary>
    /// Optional. Engine adapters that implement this take a ready WAV (synthesized
    /// server-side from the AI's sound-spec) and import it as an audio clip, optionally
    /// placing an audio-source node in the scene. Engines that don't implement it report
    /// NOT_SUPPORTED. The audio analog of <see cref="ISpriteImporter"/>.
    /// </summary>
    public interface ISoundImporter
    {
        /// <summary>Runs on the engine main thread (via the dispatcher).</summary>
        SoundImportResult ImportSound(SoundImportRequest request);
    }

    public class SoundImportRequest
    {
        public string Name { get; set; } = "sound";

        /// <summary>Raw WAV bytes (PCM, 16-bit mono) to import.</summary>
        public byte[] Wav { get; set; } = Array.Empty<byte>();

        /// <summary>0..1 playback volume for a placed source.</summary>
        public float Volume { get; set; } = 1f;

        /// <summary>Loop a placed source.</summary>
        public bool Loop { get; set; }

        /// <summary>Play the placed source immediately (for music/ambience).</summary>
        public bool AutoPlay { get; set; }

        /// <summary>If set, add an audio-source node under this scene path after import.</summary>
        public string? ScenePath { get; set; }

        /// <summary>Optional world/local position for the placed source (for spatial audio).</summary>
        public float? PosX { get; set; }
        public float? PosY { get; set; }
        public float? PosZ { get; set; }
    }

    public class SoundImportResult
    {
        /// <summary>Engine asset path of the imported audio clip.</summary>
        public string AssetPath { get; set; } = "";

        /// <summary>Scene path of the placed source node, if one was created.</summary>
        public string? NodePath { get; set; }

        public string Message { get; set; } = "";
    }
}
