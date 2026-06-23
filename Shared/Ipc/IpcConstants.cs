namespace AkerMcp.Shared.Ipc
{
    public static class IpcConstants
    {
        public const string PipePrefix = "aker-mcp-";
        public const string DiscoveryDirectory = "aker-mcp";
        public const string ProtocolVersion = "1.3.0"; // Bump on any IPC schema change
        // 1.2.0: added windowTitlePrefix to GetWindowInfo payload (Mac fallback support)
        // 1.3.0: added platform/build methods (list_platforms, get/set_platform_settings,
        //        switch_build_target, build_player) backed by the optional IBuildManager

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

            // Platform / build (backed by the optional IBuildManager).
            public const string ListPlatforms = "list_platforms";
            public const string GetPlatformSettings = "get_platform_settings";
            public const string SetPlatformSettings = "set_platform_settings";
            public const string SwitchBuildTarget = "switch_build_target";
            public const string BuildPlayer = "build_player";
        }

        public static class ErrorCodes
        {
            public const string NotSupported = "NOT_SUPPORTED";
        }
    }
}
