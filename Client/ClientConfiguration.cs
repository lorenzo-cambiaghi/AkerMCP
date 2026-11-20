namespace AkerMcp.Client
{
    public class ClientConfiguration
    {
        public string? PipeName { get; set; }

        /// <summary>
        /// Whether the execute tool runs code at all. Read by the request handler, which answers FORBIDDEN when it is off;
        /// on by default, since execute is the documented way to do anything the dedicated tools do not cover.
        /// </summary>
        public bool EnableExecuteTool { get; set; } = true;

        public int MaxInspectionDepth { get; set; } = 3;
        public int MaxQueryResults { get; set; } = 100;
    }
}
