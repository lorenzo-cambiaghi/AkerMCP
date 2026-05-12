namespace AkerMcp.Shared.Ipc
{
    public static class IpcConstants
    {
        public const string PipePrefix = "aker-mcp-";
        public const string DiscoveryDirectory = "aker-mcp";

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
        }
    }
}
