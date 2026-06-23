using System.Collections.Generic;

namespace AkerMcp.Shared.Abstraction
{
    /// <summary>
    /// Optional. Engine adapters that implement this expose platform/build
    /// operations (switch target, read/write platform settings, produce a build)
    /// in an engine-neutral way. "Android", "iOS", etc. are just platform name
    /// strings — nothing platform-specific lives in this abstraction.
    /// Engines that don't implement it cause the related tools to report
    /// NOT_SUPPORTED rather than failing.
    /// </summary>
    public interface IBuildManager
    {
        /// <summary>All platforms the engine knows about, flagged active/buildable.</summary>
        IReadOnlyList<PlatformInfo> GetPlatforms();

        /// <summary>Current settings for a platform as a flat key-value map.</summary>
        BuildSettingsResult GetPlatformSettings(string platform);

        /// <summary>Apply a subset of settings; unknown keys are reported, not fatal.</summary>
        BuildSettingsResult SetPlatformSettings(string platform, IDictionary<string, string> values);

        /// <summary>
        /// Make <paramref name="platform"/> the active build target. May trigger a
        /// reimport/domain reload on some engines (handled like refresh_scripts).
        /// </summary>
        PlatformSwitchResult SwitchPlatform(string platform);

        /// <summary>Produce a build. Typically long-running (seconds to minutes).</summary>
        BuildResult Build(BuildRequest request);
    }

    public class PlatformInfo
    {
        /// <summary>Engine-neutral platform id, e.g. "Android", "iOS", "Windows".</summary>
        public string Name { get; set; } = "";
        /// <summary>True if this is the currently active build target.</summary>
        public bool IsActive { get; set; }
        /// <summary>True if the engine can build for it on this machine (module installed).</summary>
        public bool IsSupported { get; set; }
    }

    public class BuildSettingsResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string Platform { get; set; } = "";
        /// <summary>Current platform settings as a flat string map.</summary>
        public Dictionary<string, string> Settings { get; set; } = new Dictionary<string, string>();
        /// <summary>Keys passed to SetPlatformSettings that the engine did not recognize.</summary>
        public List<string>? UnknownKeys { get; set; }
    }

    public class PlatformSwitchResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string ActivePlatform { get; set; } = "";
    }

    public class BuildRequest
    {
        public string Platform { get; set; } = "";
        /// <summary>Output file or directory path for the build artifact.</summary>
        public string OutputPath { get; set; } = "";
        /// <summary>Development/debug build (vs release).</summary>
        public bool Development { get; set; }
        /// <summary>Optional explicit scene/level list; engine default is used when null.</summary>
        public string[]? Scenes { get; set; }
    }

    public class BuildResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string? OutputPath { get; set; }
        public int Errors { get; set; }
        public int Warnings { get; set; }
        public long SizeBytes { get; set; }
        public double DurationSeconds { get; set; }
        /// <summary>Short human-readable summary of the build outcome.</summary>
        public string? Summary { get; set; }
    }
}
