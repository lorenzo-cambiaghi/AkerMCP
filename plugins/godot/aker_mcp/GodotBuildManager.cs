#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.GodotAdapter
{
    /// <summary>
    /// Maps the engine-neutral IBuildManager onto Godot's export system. Godot builds
    /// from named export presets in export_presets.cfg (there is no global "active build
    /// target" like Unity), so the neutral "platform" parameter is matched against the
    /// preset name. Settings are the preset's option key-values; builds run the editor
    /// binary headless (--export-release/-debug).
    /// </summary>
    public class GodotBuildManager : IBuildManager
    {
        private const string PresetsPath = "res://export_presets.cfg";

        public IReadOnlyList<PlatformInfo> GetPlatforms()
        {
            var list = new List<PlatformInfo>();
            var cfg = LoadPresets();
            if (cfg == null) return list;

            foreach (var (_, name, _) in ReadPresets(cfg))
                list.Add(new PlatformInfo { Name = name, IsActive = false, IsSupported = true });
            return list;
        }

        public BuildSettingsResult GetPlatformSettings(string platform)
        {
            var cfg = LoadPresets();
            if (cfg == null) return NoPresets(platform);
            if (!TryFindPreset(cfg, platform, out var section, out var godotPlatform))
                return NotFound(platform);

            var settings = new Dictionary<string, string> { ["__godotPlatform"] = godotPlatform };
            var optSection = section + ".options";
            if (cfg.HasSection(optSection))
            {
                foreach (var key in cfg.GetSectionKeys(optSection))
                    settings[key] = cfg.GetValue(optSection, key).ToString();
            }
            return new BuildSettingsResult { Success = true, Platform = platform, Settings = settings };
        }

        public BuildSettingsResult SetPlatformSettings(string platform, IDictionary<string, string> values)
        {
            var cfg = LoadPresets();
            if (cfg == null) return NoPresets(platform);
            if (!TryFindPreset(cfg, platform, out var section, out _))
                return NotFound(platform);

            var optSection = section + ".options";
            foreach (var kv in values)
            {
                if (kv.Key.StartsWith("__")) continue; // informational keys are read-only
                cfg.SetValue(optSection, kv.Key, Coerce(kv.Value));
            }

            var saveErr = cfg.Save(PresetsPath);
            if (saveErr != Error.Ok)
                return new BuildSettingsResult { Success = false, Platform = platform, Error = $"Failed to save export presets: {saveErr}" };

            return GetPlatformSettings(platform);
        }

        public PlatformSwitchResult SwitchPlatform(string platform)
        {
            // Godot has no global active build target — the platform is chosen per export
            // preset at build time. Report this clearly rather than pretending to switch.
            return new PlatformSwitchResult
            {
                Success = false,
                ActivePlatform = "",
                Error = "Godot has no active build target to switch. Pass the export preset name directly to build_player."
            };
        }

        public BuildResult Build(BuildRequest request)
        {
            var cfg = LoadPresets();
            if (cfg == null)
                return new BuildResult { Success = false, Error = "No export_presets.cfg found. Create an export preset in the editor first." };
            if (!TryFindPreset(cfg, request.Platform, out _, out _))
                return new BuildResult { Success = false, Error = $"Unknown export preset '{request.Platform}'. Call list_platforms." };

            string outAbs = ResolveOutputPath(request.OutputPath);
            string exe = OS.GetExecutablePath();
            var args = new[]
            {
                "--headless",
                "--path", ProjectSettings.GlobalizePath("res://"),
                request.Development ? "--export-debug" : "--export-release",
                request.Platform,
                outAbs
            };

            // OS.Execute blocks until the process exits and captures its stdout/stderr.
            // NOTE: this spawns a second headless editor against the same project; if the
            // open editor holds locks it can fail — building from a clean state is safest.
            var stdout = new Godot.Collections.Array();
            var sw = Stopwatch.StartNew();
            var exit = OS.Execute(exe, args, stdout, readStderr: true);
            sw.Stop();

            var log = new System.Text.StringBuilder();
            foreach (var line in stdout) log.AppendLine(line.ToString());

            bool fileExists = System.IO.File.Exists(outAbs);
            bool ok = exit == 0 && fileExists;
            long size = fileExists ? new System.IO.FileInfo(outAbs).Length : 0;

            return new BuildResult
            {
                Success = ok,
                OutputPath = ok ? outAbs : null,
                SizeBytes = size,
                DurationSeconds = sw.Elapsed.TotalSeconds,
                Summary = ok ? $"Export succeeded → {outAbs}" : $"Export exited with code {exit}",
                Error = ok ? null : $"Export failed (exit {exit}).\n{Tail(log.ToString(), 1500)}"
            };
        }

        // --- helpers -------------------------------------------------------------

        private static ConfigFile? LoadPresets()
        {
            var cfg = new ConfigFile();
            return cfg.Load(PresetsPath) == Error.Ok ? cfg : null;
        }

        private static IEnumerable<(string section, string name, string platform)> ReadPresets(ConfigFile cfg)
        {
            foreach (var section in cfg.GetSections())
            {
                if (section.StartsWith("preset.") && !section.EndsWith(".options"))
                {
                    var name = cfg.GetValue(section, "name", "").AsString();
                    var platform = cfg.GetValue(section, "platform", "").AsString();
                    yield return (section, name, platform);
                }
            }
        }

        private static bool TryFindPreset(ConfigFile cfg, string platform, out string section, out string godotPlatform)
        {
            foreach (var (sec, name, plat) in ReadPresets(cfg))
            {
                if (string.Equals(name, platform, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(plat, platform, StringComparison.OrdinalIgnoreCase))
                {
                    section = sec;
                    godotPlatform = plat;
                    return true;
                }
            }
            section = "";
            godotPlatform = "";
            return false;
        }

        private static string ResolveOutputPath(string path)
        {
            if (path.StartsWith("res://") || path.StartsWith("user://"))
                return ProjectSettings.GlobalizePath(path);
            if (System.IO.Path.IsPathRooted(path))
                return path;
            return ProjectSettings.GlobalizePath("res://" + path.TrimStart('/'));
        }

        private static Variant Coerce(string v)
        {
            if (bool.TryParse(v, out var b)) return b;
            if (long.TryParse(v, out var l)) return l;
            if (double.TryParse(v, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
            return v;
        }

        private static string Tail(string s, int max)
            => s.Length <= max ? s : "…" + s.Substring(s.Length - max);

        private static BuildSettingsResult NotFound(string platform) => new BuildSettingsResult
        {
            Success = false,
            Platform = platform,
            Error = $"Unknown export preset '{platform}'. Call list_platforms for valid names."
        };

        private static BuildSettingsResult NoPresets(string platform) => new BuildSettingsResult
        {
            Success = false,
            Platform = platform,
            Error = "No export_presets.cfg found. Create an export preset in the editor first."
        };
    }
}
