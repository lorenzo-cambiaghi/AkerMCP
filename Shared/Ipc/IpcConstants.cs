namespace AkerMcp.Shared.Ipc
{
    public static class IpcConstants
    {
        public const string PipePrefix = "aker-mcp-";
        public const string DiscoveryDirectory = "aker-mcp";
        // The largest message either side writes or accepts. A reply over it is answered with an error instead.
        public const int MaxFrameBytes = 64 * 1024 * 1024;
        // Bump the MINOR on an addition an older peer can ignore, the MAJOR on a change that
        // breaks one. The server compares only the major (EngineConnection.DescribeVersionSkew):
        // a plugin one minor behind speaks a subset, which is what "optional capability" has
        // meant here all along.
        public const string ProtocolVersion = "1.7.0";
        // 1.2.0: added windowTitlePrefix to GetWindowInfo payload (Mac fallback support)
        // 1.3.0: added platform/build methods (list_platforms, get/set_platform_settings,
        //        switch_build_target, build_player) backed by the optional IBuildManager
        // 1.4.0: added import_sprite + IpcRequest.Binary (inbound image bytes) backed by
        //        the optional ISpriteImporter
        // 1.5.0: added scene management (new_scene/open_scene/save_scene) backed by the
        //        optional ISceneManager, and write_script (engine-side project-rooted file write)
        // 1.6.0: added the runtime loop — play control (enter_play/exit_play/set_play_pause/
        //        play_step/get_play_state) backed by the optional IPlayModeController, and
        //        send_input backed by the optional IInputSimulator (with an OS-level fallback);
        //        plus import_sound (create_sound) backed by the optional ISoundImporter — a
        //        server-synthesized WAV shipped over the inbound binary channel like import_sprite
        // 1.7.0: added get_consent, backed by the optional IAgentConsent, so an engine that
        //        lets the user revoke an agent's access can say "refused" where it could only
        //        say "no image" before — and the server declines instead of taking the picture
        //        itself from the OS. Additive: an engine without it answers NOT_SUPPORTED and
        //        is never asked again for that connection.

        public static class Methods
        {
            public const string Ping = "ping";
            public const string Inspect = "inspect";
            public const string GetProperty = "get_property";
            public const string SetProperty = "set_property";
            public const string CallMethod = "call_method";
            public const string Query = "query";
            public const string Create = "create";
            public const string Delete = "delete";
            public const string Execute = "execute";
            public const string GetSceneHierarchy = "get_scene_hierarchy";
            public const string GetProjectInfo = "get_project_info";
            public const string GetRecentLogs = "get_recent_logs";
            public const string GetEngineTypes = "get_engine_types";

            public const string RefreshScripts = "refresh_scripts";
            public const string GetCompileStatus = "get_compile_status";
            public const string GetCompileErrors = "get_compile_errors";
            public const string GetConsoleLogs = "get_console_logs";
            public const string ClearConsole = "clear_console";
            public const string SelectObject = "select_object";
            public const string GetSelection = "get_selection";
            public const string TakeScreenshot = "take_screenshot";
            public const string GetWindowInfo = "get_window_info";
            public const string ImportSprite = "import_sprite";
            public const string NewScene = "new_scene";
            public const string OpenScene = "open_scene";
            public const string SaveScene = "save_scene";
            public const string WriteScript = "write_script";

            // Platform / build (backed by the optional IBuildManager).
            public const string ListPlatforms = "list_platforms";
            public const string GetPlatformSettings = "get_platform_settings";
            public const string SetPlatformSettings = "set_platform_settings";
            public const string SwitchBuildTarget = "switch_build_target";
            public const string BuildPlayer = "build_player";

            // Runtime loop: play control (optional IPlayModeController) + input (optional IInputSimulator).
            public const string EnterPlay = "enter_play";
            public const string ExitPlay = "exit_play";
            public const string SetPlayPause = "set_play_pause";
            public const string PlayStep = "play_step";
            public const string GetPlayState = "get_play_state";
            public const string SendInput = "send_input";

            // Procedural audio: import a server-synthesized WAV (optional ISoundImporter).
            public const string ImportSound = "import_sound";

            // Whether the agent may act at all (optional IAgentConsent). Asked once per
            // connection, and thereafter only before the server would reach around the
            // engine to the OS.
            public const string GetConsent = "get_consent";
        }

        public static class ErrorCodes
        {
            public const string NotSupported = "NOT_SUPPORTED";
        }
    }
}
