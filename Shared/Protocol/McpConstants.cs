namespace AkerMcp.Shared.Protocol
{
    public static class McpConstants
    {
        public const string ProtocolVersion = "2025-03-26";
        public const string JsonRpcVersion = "2.0";

        public static class Methods
        {
            public const string Initialize = "initialize";
            public const string Initialized = "notifications/initialized";
            public const string Ping = "ping";

            public const string ToolsList = "tools/list";
            public const string ToolsCall = "tools/call";
            public const string ToolsListChanged = "notifications/tools/list_changed";

            public const string ResourcesList = "resources/list";
            public const string ResourcesRead = "resources/read";
            public const string ResourcesListChanged = "notifications/resources/list_changed";
        }
    }
}
