using System.Collections.Generic;

namespace AkerMcp.Server
{
    /// <summary>
    /// Every tool description the client reads, in one file, kept short on purpose.
    ///
    /// The tool list is sent once per session and then sits in the model's
    /// context on every turn, so each character here is paid for on every
    /// request. Before this file the 40 descriptions weighed 21,600 characters,
    /// most of it the inspect / modify / verify sermon repeated per tool and
    /// examples that duplicated the parameter schema. The rule now: say what the
    /// tool answers, the formats a model cannot infer (globals, event shapes,
    /// spec grammars, path syntax) and the one mistake it tends to make. The
    /// workflow lives once in <see cref="ServerInstructions"/>.
    /// </summary>
    public static class ToolDocs
    {
        public static string Describe(string name) => Descriptions[name];

        public static IReadOnlyDictionary<string, string> Descriptions { get; } = new Dictionary<string, string>
        {
            // ---- scene: read ---------------------------------------------------
            ["inspect"] =
                "Components, properties, methods and children of a scene object (path like '/Player') " +
                "or of a type ('Rigidbody'). depth=2 includes the children's properties, " +
                "include_methods lists callable methods, filter is a regex on names.",

            ["get_property"] =
                "Read one property by dot path: transform properties without prefix ('position', " +
                "'eulerAngles'), other components with a type prefix ('Rigidbody.mass'), nested paths " +
                "('MeshRenderer.material.color.r'). Structs come back as JSON objects.",

            ["query"] =
                "Find objects by type ('Camera'), name glob or regex ('Enemy*'), tag, or property " +
                "values; filters combine. Returns the matches with their scene paths, up to " +
                "max_results (default 50).",

            ["get_selection"] =
                "The object currently selected in the editor: path, components and a property summary.",

            ["get_console_logs"] =
                "Recent engine console entries. level_filter narrows to error, warning or info; " +
                "search keeps entries containing a text; count sets how many (default 50).",

            ["get_compile_errors"] =
                "The last compilation's status, errors and warnings with file, line and column, " +
                "without recompiling. Warnings are capped at 10; errors_only skips them.",

            ["take_screenshot"] =
                "Screenshot of the editor as JPEG (max 1920 px, quality 85). view='game' (default) is " +
                "what the player sees, 'scene' is the editor view with gizmos. Use it after visual " +
                "changes (placement, materials, lighting, UI, spawned content), not for non-visual " +
                "properties and not to check compilation.",

            // ---- scene: edit ---------------------------------------------------
            ["set_property"] =
                "Set one property by dot path (same syntax as get_property), with undo. Values: " +
                "primitives, or JSON objects for structs: vector {\"x\":1,\"y\":2,\"z\":3}, colour " +
                "{\"r\":1,\"g\":0,\"b\":0,\"a\":1}, quaternion {\"x\":0,\"y\":0,\"z\":0,\"w\":1}. For many " +
                "objects use one execute script instead.",

            ["call_method"] =
                "Invoke a method on a scene object ('/Player', 'SetActive', ['false']) or a static " +
                "member of a fully qualified type ('UnityEngine.Application', 'get_dataPath'). " +
                "Arguments are strings, converted to the parameter types.",

            ["create"] =
                "Add an object or node to the scene, with undo: type (engine type name), name, " +
                "optional parent_path and initial properties ({\"position\":{\"x\":0,\"y\":5,\"z\":0}}). " +
                "For many objects or procedural content use execute.",

            ["delete"] =
                "Remove an object from the scene, with undo. recursive=true (default) removes its " +
                "children too; recursive=false re-parents them to the deleted object's parent.",

            ["select"] =
                "Select an object in the editor hierarchy and inspector. It becomes selectedObject " +
                "in execute scripts.",

            ["refresh_scripts"] =
                "Compile pending script changes and return the result, errors and warnings included; " +
                "blocks through Unity's domain reload (5 to 60 s). Call it after creating or editing " +
                "a .cs file; get_compile_errors is not needed afterwards.",

            ["execute"] =
                "Run C# on the engine's main thread through Roslyn and return its value; console " +
                "output is captured too. Globals: selectedObject, Find(name), FindAll<T>(), " +
                "Create(name), Log(msg). Pre-imported: System, System.Collections.Generic, System.Linq " +
                "and the engine namespaces; other `using` lines go at the top and are hoisted, as are " +
                "class, struct, interface, enum, record and delegate declarations (a MonoBehaviour " +
                "declared here can be added with AddComponent). Nothing persists between calls, so " +
                "re-acquire what you need and end with a return. timeout_ms (default 5000) only stops " +
                "the wait: a running script cannot be aborted.",

            ["write_script"] =
                "Write a source file into the project by a path relative to the project root " +
                "('Assets/Scripts/Bird.cs', 'scripts/bird.gd'), creating folders; the engine resolves " +
                "the root, wherever this server runs. Follow with refresh_scripts for C#.",

            // ---- authoring -----------------------------------------------------
            ["create_sprite"] =
                "Author a flat, geometric 2D placeholder as a JSON shape-spec; the server rasterises " +
                "it to a PNG and imports it as a sprite on any engine, optionally placed at scene_path. " +
                "Spec: {\"width\":64,\"height\":64,\"background\":null,\"shapes\":[...]} drawn in order. " +
                "Shapes: ellipse (cx,cy,rx,ry), rect (x,y,w,h,rx), polygon (points [[x,y],...]), line " +
                "(points), path (d: M L Q C Z). Each takes fill and/or stroke, strokeWidth, opacity 0..1; " +
                "a paint is a hex colour or {\"gradient\":\"linear\",\"x1\":0,\"y1\":0,\"x2\":0,\"y2\":64," +
                "\"stops\":[{\"offset\":0,\"color\":\"#fff\"},{\"offset\":1,\"color\":\"#888\"}]}. Keep " +
                "silhouettes clean: recognisable beats detailed.",

            ["create_sound"] =
                "Synthesise a short placeholder sound from a jsfxr-style spec and import it as an audio " +
                "clip on any engine: wave square|saw|sine|triangle|noise, freq Hz, freq_sweep Hz per " +
                "second (negative sweeps down), attack/sustain/decay seconds, duration, volume 0..1, " +
                "vibrato_depth, vibrato_rate. Recipes: jump = square 480 sweep +1400, short; hit = noise " +
                "200 decay 0.25; laser = saw 1200 sweep -3000. Top-level scene_path, position, volume, " +
                "loop and auto_play place a source.",

            ["add_primitive"] =
                "Write a vetted gameplay script for the connected engine instead of hand-writing it. " +
                "No id lists the catalog (platformer_controller_2d, auto_runner_2d, camera_follow_2d, " +
                "killzone_2d, score_overlay); id writes it (path optional). Then refresh_scripts, add " +
                "the component (execute: obj.AddComponent<T>()) and set its fields. Unity primitives " +
                "use the legacy Input Manager.",

            ["new_scene"] =
                "Create a fresh scene; two_d=true (default) sets up a 2D camera. save_path (an engine " +
                "asset path such as 'Assets/Scenes/Flappy.unity' or 'res://scenes/flappy.tscn') saves " +
                "it. Reports NOT_SUPPORTED on engines without it (Stride).",

            ["open_scene"] =
                "Open a scene by its engine asset path ('Assets/Scenes/Main.unity', 'res://scenes/main.tscn').",

            ["save_scene"] =
                "Save the edited scene in place, or to path.",

            // ---- runtime loop and verification ---------------------------------
            ["enter_play"] =
                "Run the project: play mode in a game engine, the timeline in an animation editor. " +
                "Waits through Unity's domain reload and reports the settled state. Godot runs the game " +
                "in its own window: screenshot it with capture_window. The loop: enter_play, send_input, " +
                "capture_sequence or sample_state, exit_play.",

            ["exit_play"] =
                "Stop play and return to edit mode. Do it when verification is done.",

            ["get_play_state"] =
                "Playing or paused, time, clip duration, frame counter and fps. Read it twice: a frozen " +
                "game keeps isPlaying true with a stale frameCount. windowTitle names a separate game " +
                "window (Godot), which send_input and capture_sequence then target.",

            ["set_play_pause"] =
                "Pause (true) or resume (false) play. Pause before play_step.",

            ["play_step"] =
                "Advance play while paused: SkelForge steps N frames exactly, Unity one editor tick per " +
                "call, so repeat and confirm with get_play_state.",

            ["capture_sequence"] =
                "count screenshots (1-8, default 4) at interval_ms (0-3000, default 500) while playing, " +
                "returned as a strip to see motion. view game|scene; window_title captures an OS window " +
                "instead, for a game in its own window (Godot, Stride).",

            ["send_input"] =
                "Inject input into the running game: an ordered events list of key " +
                "({\"type\":\"key\",\"key\":\"Space\",\"hold_ms\":60}, or pressed true/false for explicit " +
                "down and up), mouse_button ({\"button\":\"left\",\"hold_ms\":50}) and mouse_move " +
                "({\"x\":960,\"y\":540}, pixels from the top-left). Keys: Space, Enter, Escape, Tab, " +
                "Up/Down/Left/Right, A-Z, 0-9, Shift, Ctrl, Alt. Unity receives it in-process in the " +
                "Game View; Godot and Stride run the game in a separate window, so pass window_title or " +
                "the editor receives it. OS-level mouse coordinates reach the primary monitor only; " +
                "prefer keys there.",

            ["sample_state"] =
                "Evaluate C# expressions in the running game and return their values, one per probe " +
                "name ({\"birdY\":\"Find(\\\"Bird\\\").transform.position.y\"}); prefer scalars. The " +
                "honest way to check a mechanic, instead of reading pixels.",

            ["assert_state"] =
                "Check runtime conditions during play: each assertion evaluates a C# expression and " +
                "compares it with op (==, !=, <, <=, >, >=, approx within 1e-3, truthy, falsy) to " +
                "value, with a label. Polls every poll_ms until all pass or timeout_ms elapses (0 = " +
                "check once), so 'becomes true' conditions work.",

            ["playtest"] =
                "Drive and verify the game in one call, server-side, so input, capture and asserts land " +
                "at precise moments: enter_play, run steps in order, check criteria, exit_play. Steps: " +
                "{\"input\":{\"events\":[...]}} (window_title for Godot/Stride), {\"wait_ms\":300}, " +
                "{\"capture\":true} or {\"capture\":{\"view\":\"game\"}}, {\"assert\":[...],\"timeout_ms\":1000}, " +
                "{\"sample\":{\"y\":\"expr\"}}; criteria is a final assert list. Returns PASS or FAIL, a " +
                "timeline, the captured frames and the evidence.",

            // ---- platform and build --------------------------------------------
            ["list_platforms"] =
                "The build platforms the engine knows, each flagged as the active target and as " +
                "buildable on this machine. Source of the platform names the other build tools take.",

            ["get_platform_settings"] =
                "A platform's build and player settings as a flat key-value map. Keys differ per " +
                "engine, so read them here before set_platform_settings.",

            ["set_platform_settings"] =
                "Change some of a platform's build and player settings; pass only the keys to change " +
                "(keys from get_platform_settings). Unknown keys are reported in unknownKeys, not fatal.",

            ["switch_build_target"] =
                "Make a platform the active build target. Can trigger a recompile and domain reload " +
                "and blocks until done (seconds to a minute). Switch before building for another platform.",

            ["build_player"] =
                "Build the project for a platform to output_path (APK, AAB, exe, app bundle); " +
                "long-running. Returns result, error and warning counts, output path and artifact size. " +
                "The platform should be the active target first.",

            // ---- server and OS -------------------------------------------------
            ["engine_status"] =
                "Which engine answers these tools, which others are running, and optionally pin one " +
                "(engine='unity'; an empty string unpins). A pin survives reconnects and the server stays " +
                "disconnected rather than switching editors; AKER_MCP_ENGINE does the same. Call it when " +
                "execute compiles fine but the engine's own types are missing.",

            ["list_windows"] =
                "Visible top-level windows on the machine running the server: title, process name, " +
                "pid. Works without an engine; the titles feed capture_window, focus_window and send_input.",

            ["capture_window"] =
                "JPEG screenshot of any visible window on the server's machine, matched by a " +
                "case-insensitive substring of its title (first match; occluded windows are captured " +
                "without stealing focus). For external apps, or a game that runs in its own window.",

            ["focus_window"] =
                "Bring a window to the foreground, matched by a title substring, restoring it if " +
                "minimised. Needed before editors whose screenshot requires the foreground (Stride), " +
                "and to reach a modal dialog.",
        };
    }
}
