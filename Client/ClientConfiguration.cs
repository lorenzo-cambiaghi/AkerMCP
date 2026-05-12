namespace AkerMcp.Client
{
    public class ClientConfiguration
    {
        public string? PipeName { get; set; }
        public int RequestTimeoutMs { get; set; } = 30000;
        public bool EnableExecuteTool { get; set; } = false;
        public int MaxInspectionDepth { get; set; } = 3;
        public int MaxQueryResults { get; set; } = 100;
    }
}
