#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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

            foreach (var preset in ReadPresets(cfg))
                list.Add(new PlatformInfo { Name = preset.Name, IsActive = false, IsSupported = true });
            return list;
        }

        public BuildSettingsResult GetPlatformSettings(string platform)
        {
            var cfg = LoadPresets();
            if (cfg == null) return NoPresets(platform);
            if (!TryFindPreset(cfg, platform, out var preset))
                return NotFound(platform);

            var settings = new Dictionary<string, string>
            {
                ["__godotPlatform"] = preset.Platform,
                ["__presetName"] = preset.Name,
            };
            var optSection = preset.Section + ".options";
            if (cfg.HasSection(optSection))
            {
                foreach (var key in cfg.GetSectionKeys(optSection))
                    settings[key] = Format(cfg.GetValue(optSection, key));
            }
            return new BuildSettingsResult { Success = true, Platform = platform, Settings = settings };
        }

        public BuildSettingsResult SetPlatformSettings(string platform, IDictionary<string, string> values)
        {
            var cfg = LoadPresets();
            if (cfg == null) return NoPresets(platform);
            if (!TryFindPreset(cfg, platform, out var preset))
                return NotFound(platform);

            var optSection = preset.Section + ".options";
            var unknown = new List<string>();
            var rejected = new List<string>();
            foreach (var kv in values)
            {
                if (kv.Key.StartsWith("__")) continue; // informational keys are read-only

                // Only options the preset already has: the export dialog writes every option of the
                // platform, so a key that is not there is a typo or belongs to another platform, and
                // writing it would leave a dead line in export_presets.cfg.
                if (!cfg.HasSectionKey(optSection, kv.Key))
                {
                    unknown.Add(kv.Key);
                    continue;
                }

                if (TryCoerce(kv.Value, cfg.GetValue(optSection, kv.Key), out var value))
                    cfg.SetValue(optSection, kv.Key, value);
                else
                    rejected.Add($"{kv.Key}='{kv.Value}' (expects {cfg.GetValue(optSection, kv.Key).VariantType})");
            }

            var saveErr = cfg.Save(PresetsPath);
            if (saveErr != Error.Ok)
                return new BuildSettingsResult { Success = false, Platform = platform, Error = $"Failed to save export presets: {saveErr}" };

            var result = GetPlatformSettings(platform);
            if (unknown.Count > 0) result.UnknownKeys = unknown;
            if (rejected.Count > 0) result.Error = "Not applied, the value does not fit the option's type: " + string.Join(", ", rejected);
            return result;
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
            if (!TryFindPreset(cfg, request.Platform, out var preset))
                return new BuildResult { Success = false, Error = $"Unknown export preset '{request.Platform}'. Call list_platforms." };

            string outAbs = ResolveOutputPath(request.OutputPath);
            string exe = OS.GetExecutablePath();
            var args = new[]
            {
                "--headless",
                "--path", ProjectSettings.GlobalizePath("res://"),
                request.Development ? "--export-debug" : "--export-release",
                // The preset's exact name. The preset is matched loosely (any case, or by platform),
                // but the CLI wants the name as written: "windows" for a preset called
                // "Windows Desktop" failed with "Invalid export preset name".
                preset.Name,
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

            // Godot exports what the preset's resource filter selects; there is no per-build scene
            // list to pass. Said out loud, so a caller does not assume its list was honoured.
            string scenesNote = request.Scenes is { Length: > 0 }
                ? " Note: 'scenes' is ignored on Godot; the preset's Resources filter decides what is exported."
                : "";

            return new BuildResult
            {
                Success = ok,
                OutputPath = ok ? outAbs : null,
                SizeBytes = size,
                DurationSeconds = sw.Elapsed.TotalSeconds,
                Summary = (ok ? $"Export of preset '{preset.Name}' succeeded → {outAbs}" : $"Export exited with code {exit}") + scenesNote,
                Error = ok ? null : $"Export of preset '{preset.Name}' failed (exit {exit}).\n{Tail(log.ToString(), 1500)}"
            };
        }

        // --- helpers -------------------------------------------------------------

        private readonly struct Preset
        {
            public Preset(string section, string name, string platform)
            {
                Section = section;
                Name = name;
                Platform = platform;
            }

            public string Section { get; }
            public string Name { get; }
            public string Platform { get; }
        }

        private static ConfigFile? LoadPresets()
        {
            var cfg = new ConfigFile();
            return cfg.Load(PresetsPath) == Error.Ok ? cfg : null;
        }

        private static IEnumerable<Preset> ReadPresets(ConfigFile cfg)
        {
            foreach (var section in cfg.GetSections())
            {
                if (section.StartsWith("preset.") && !section.EndsWith(".options"))
                {
                    var name = cfg.GetValue(section, "name", "").AsString();
                    var platform = cfg.GetValue(section, "platform", "").AsString();
                    yield return new Preset(section, name, platform);
                }
            }
        }

        // An exact preset name wins over a platform match, so "Windows Desktop" still picks that
        // preset when another one happens to be named after a platform.
        private static bool TryFindPreset(ConfigFile cfg, string platform, out Preset found)
        {
            Preset? byPlatform = null;
            foreach (var preset in ReadPresets(cfg))
            {
                if (string.Equals(preset.Name, platform, StringComparison.OrdinalIgnoreCase))
                {
                    found = preset;
                    return true;
                }
                if (byPlatform == null && string.Equals(preset.Platform, platform, StringComparison.OrdinalIgnoreCase))
                    byPlatform = preset;
            }
            found = byPlatform ?? default;
            return byPlatform != null;
        }

        private static string ResolveOutputPath(string path)
        {
            if (path.StartsWith("res://") || path.StartsWith("user://"))
                return ProjectSettings.GlobalizePath(path);
            if (System.IO.Path.IsPathRooted(path))
                return path;
            return ProjectSettings.GlobalizePath("res://" + path.TrimStart('/'));
        }

        // The type comes from the value the option already holds, not from the look of the text:
        // guessing turned a version string "1.10" into the float 1.1 (and "1.0" into 1), which
        // export_presets.cfg then kept as a number.
        private static bool TryCoerce(string text, Variant existing, out Variant value)
        {
            var inv = CultureInfo.InvariantCulture;
            switch (existing.VariantType)
            {
                case Variant.Type.Bool:
                    if (bool.TryParse(text, out var b)) { value = b; return true; }
                    if (text == "0" || text == "1") { value = text == "1"; return true; }
                    break;
                case Variant.Type.Int:
                    if (long.TryParse(text, NumberStyles.Integer, inv, out var l)) { value = l; return true; }
                    break;
                case Variant.Type.Float:
                    if (double.TryParse(text, NumberStyles.Float, inv, out var d)) { value = d; return true; }
                    break;
                case Variant.Type.String:
                case Variant.Type.StringName:
                case Variant.Type.NodePath:
                case Variant.Type.Nil:
                    value = text;
                    return true;
                default:
                    // Arrays, dictionaries, packed arrays: Godot's own literal form, as Format
                    // shows them, e.g. PackedStringArray("a", "b").
                    var parsed = GD.StrToVar(text);
                    if (parsed.VariantType == existing.VariantType) { value = parsed; return true; }
                    break;
            }
            value = default;
            return false;
        }

        // Scalars as plain text; anything else in Godot's own literal form (PackedStringArray("a", "b")),
        // which is what TryCoerce parses back — the plain form ("[a, b]") would not round-trip.
        private static string Format(Variant v)
        {
            switch (v.VariantType)
            {
                case Variant.Type.Nil:
                case Variant.Type.Bool:
                case Variant.Type.Int:
                case Variant.Type.Float:
                case Variant.Type.String:
                case Variant.Type.StringName:
                case Variant.Type.NodePath:
                    return v.ToString();
                default:
                    return GD.VarToStr(v);
            }
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
#endif
