using System.Collections.Generic;
using AkerMcp.Shared.Protocol;

namespace AkerMcp.Server
{
    /// <summary>
    /// The four MCP hints for every tool, in one place.
    ///
    /// A client that sees no hint assumes the worst the spec allows: not read-only,
    /// destructive, not idempotent, open-world. Until this table existed 18 of the
    /// 40 tools shipped no annotations at all, and the ones that did shipped a
    /// single flag, so "readOnlyHint: true" reached the client while
    /// "destructiveHint: false" never did (a false was not serialised). Clients use
    /// these hints to auto-approve reads and to ask before destructive calls, so a
    /// missing or half-declared hint costs a confirmation prompt on every
    /// inspect, or hides a real risk behind a default.
    ///
    /// Read-only means the call changes nothing in the editor or on disk. Destructive
    /// means it can lose work or run arbitrary code: delete, execute, call_method,
    /// send_input (keystrokes into any window), write_script (overwrites a file),
    /// new_scene / open_scene (replace the open scene), build_player and
    /// switch_build_target (long, and the author marked them so). Undoable edits
    /// (set_property, create, the authoring tools) are writes but not destructive.
    /// Nothing here reaches outside the machine, so openWorldHint is false throughout.
    /// </summary>
    public static class ToolAnnotationTable
    {
        private static ToolAnnotations Read() => new ToolAnnotations
        { ReadOnlyHint = true, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = false };

        private static ToolAnnotations Edit(bool idempotent) => new ToolAnnotations
        { ReadOnlyHint = false, DestructiveHint = false, IdempotentHint = idempotent, OpenWorldHint = false };

        private static ToolAnnotations Destructive(bool idempotent) => new ToolAnnotations
        { ReadOnlyHint = false, DestructiveHint = true, IdempotentHint = idempotent, OpenWorldHint = false };

        private static readonly Dictionary<string, ToolAnnotations> Table = new Dictionary<string, ToolAnnotations>
        {
            // scene: read
            ["inspect"] = Read(),
            ["get_property"] = Read(),
            ["query"] = Read(),
            ["get_selection"] = Read(),
            ["get_console_logs"] = Read(),
            ["get_compile_errors"] = Read(),
            ["take_screenshot"] = Read(),
            // scene: edit (undoable)
            ["set_property"] = Edit(idempotent: true),
            ["create"] = Edit(idempotent: false),
            ["select"] = Edit(idempotent: true),
            ["refresh_scripts"] = Edit(idempotent: true),
            // scene: destructive or arbitrary
            ["delete"] = Destructive(idempotent: true),
            ["call_method"] = Destructive(idempotent: false),
            ["execute"] = Destructive(idempotent: false),
            ["write_script"] = Destructive(idempotent: true),
            // authoring
            ["create_sprite"] = Edit(idempotent: false),
            ["create_sound"] = Edit(idempotent: false),
            ["add_primitive"] = Edit(idempotent: false),
            ["new_scene"] = Destructive(idempotent: false),
            ["open_scene"] = Destructive(idempotent: true),
            ["save_scene"] = Edit(idempotent: true),
            // runtime loop and verification
            ["enter_play"] = Edit(idempotent: true),
            ["exit_play"] = Edit(idempotent: true),
            ["get_play_state"] = Read(),
            ["set_play_pause"] = Edit(idempotent: true),
            ["play_step"] = Edit(idempotent: false),
            ["capture_sequence"] = Read(),
            ["sample_state"] = Read(),
            ["assert_state"] = Read(),
            ["playtest"] = Edit(idempotent: false),
            ["send_input"] = Destructive(idempotent: false),
            // platform and build
            ["list_platforms"] = Read(),
            ["get_platform_settings"] = Read(),
            ["set_platform_settings"] = Edit(idempotent: true),
            ["switch_build_target"] = Destructive(idempotent: true),
            ["build_player"] = Destructive(idempotent: false),
            // server and OS
            ["engine_status"] = Edit(idempotent: true),   // can pin the engine: not a pure read
            ["list_windows"] = Read(),
            ["capture_window"] = Read(),
            ["focus_window"] = Edit(idempotent: true),
        };

        /// <summary>Tool names the table knows. The loopback test keeps this equal to the registered set.</summary>
        public static IReadOnlyCollection<string> Names => Table.Keys;

        /// <summary>
        /// The annotations for <paramref name="name"/>: the table wins, so a tool cannot ship a
        /// half-declared hint; an unknown name falls back to what the caller passed, or null.
        /// </summary>
        public static ToolAnnotations? For(string name, ToolAnnotations? fallback)
            => Table.TryGetValue(name, out var a) ? a : fallback;
    }
}
