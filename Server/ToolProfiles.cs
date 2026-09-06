using System;
using System.Collections.Generic;
using System.Linq;

namespace AkerMcp.Server
{
    /// <summary>
    /// Which tools a session gets. Every tool definition rides in the client's
    /// context on every turn, and forty of them cost about 9,000 tokens before
    /// the first call. Most sessions inspect, edit, run a script and look at the
    /// result; the authoring, playtest and build tools are for the sessions that
    /// need them. So the tools are layered:
    ///
    ///   core      inspect, edit, execute, screenshot, logs, compile          (14)
    ///   standard  core plus scripts, scenes, play control, engine choice and
    ///             the OS window tools that unblock a modal dialog     (27, default)
    ///   full      everything: sprite and sound authoring, playtest, builds   (40)
    ///
    /// Chosen with <c>--profile</c>, then the AKER_MCP_PROFILE variable, then the
    /// default. AKER_MCP_TOOLS_INCLUDE and AKER_MCP_TOOLS_EXCLUDE (comma-separated
    /// names) adjust a profile by name; exclude wins over include. A name the
    /// table does not know is never hidden.
    /// </summary>
    public static class ToolProfiles
    {
        public const string Default = "standard";

        public static readonly string[] Core =
        {
            "inspect", "get_property", "set_property", "call_method", "query", "create", "delete",
            "select", "get_selection", "get_console_logs", "get_compile_errors", "refresh_scripts",
            "execute", "take_screenshot",
        };

        public static readonly string[] Standard = Core.Concat(new[]
        {
            "write_script", "new_scene", "open_scene", "save_scene",
            "enter_play", "exit_play", "get_play_state", "set_play_pause",
            "engine_status", "list_windows", "capture_window", "focus_window", "send_input",
        }).ToArray();

        public static readonly string[] Full = Standard.Concat(new[]
        {
            "create_sprite", "create_sound", "add_primitive",
            "play_step", "capture_sequence", "sample_state", "assert_state", "playtest",
            "list_platforms", "get_platform_settings", "set_platform_settings",
            "switch_build_target", "build_player",
        }).ToArray();

        public static readonly IReadOnlyDictionary<string, string[]> Profiles =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["core"] = Core,
                ["standard"] = Standard,
                ["full"] = Full,
            };

        /// <summary>The profile name this process should run: argument, then environment, then default.</summary>
        public static string Resolve(string? fromArgs)
        {
            var name = !string.IsNullOrWhiteSpace(fromArgs) ? fromArgs!
                : Environment.GetEnvironmentVariable("AKER_MCP_PROFILE") ?? Default;
            name = name.Trim().ToLowerInvariant();
            if (!Profiles.ContainsKey(name))
                throw new ArgumentException(
                    $"unknown tool profile '{name}'; choose one of {string.Join(", ", Profiles.Keys)}");
            return name;
        }

        /// <summary>Comma-separated names from an environment variable, or none.</summary>
        public static string[] NamesFromEnvironment(string variable)
        {
            var raw = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
            return raw.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        }

        /// <summary>
        /// Split the registered names into (kept, dropped) for a profile. Order follows
        /// registration. A registered name the profiles never mention is kept: hiding
        /// a tool this table cannot reason about would be a silent regression.
        /// </summary>
        public static (List<string> kept, List<string> dropped) Select(
            IEnumerable<string> registered, string profile,
            IEnumerable<string>? include = null, IEnumerable<string>? exclude = null)
        {
            if (!Profiles.TryGetValue(profile, out var chosen))
                throw new ArgumentException($"unknown tool profile '{profile}'");
            var wanted = new HashSet<string>(chosen, StringComparer.Ordinal);
            foreach (var n in include ?? Array.Empty<string>()) wanted.Add(n);
            foreach (var n in exclude ?? Array.Empty<string>()) wanted.Remove(n);
            var known = new HashSet<string>(Full, StringComparer.Ordinal);

            var kept = new List<string>();
            var dropped = new List<string>();
            foreach (var name in registered)
            {
                if (wanted.Contains(name) || !known.Contains(name)) kept.Add(name);
                else dropped.Add(name);
            }
            return (kept, dropped);
        }
    }
}
