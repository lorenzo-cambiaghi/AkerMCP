using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCPSharp.Shared.Protocol
{
    public class InitializeRequestParams
    {
        [JsonPropertyName("protocolVersion")]
        public string ProtocolVersion { get; set; } = null!;

        [JsonPropertyName("capabilities")]
        public ClientCapabilities Capabilities { get; set; } = new();

        [JsonPropertyName("clientInfo")]
        public ImplementationInfo ClientInfo { get; set; } = new();
    }

    public class ClientCapabilities
    {
        [JsonPropertyName("sampling")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? Sampling { get; set; }
    }

    public class InitializeResult
    {
        [JsonPropertyName("protocolVersion")]
        public string ProtocolVersion { get; set; } = McpConstants.ProtocolVersion;

        [JsonPropertyName("capabilities")]
        public ServerCapabilities Capabilities { get; set; } = new();

        [JsonPropertyName("serverInfo")]
        public ImplementationInfo ServerInfo { get; set; } = new();
    }

    public class ServerCapabilities
    {
        [JsonPropertyName("tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ToolsCapability? Tools { get; set; }

        [JsonPropertyName("resources")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ResourcesCapability? Resources { get; set; }
    }

    public class ToolsCapability
    {
        [JsonPropertyName("listChanged")]
        public bool ListChanged { get; set; }
    }

    public class ResourcesCapability
    {
        [JsonPropertyName("subscribe")]
        public bool Subscribe { get; set; }

        [JsonPropertyName("listChanged")]
        public bool ListChanged { get; set; }
    }

    public class ImplementationInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "MCPSharp";

        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0.0";
    }

    public class ToolDefinition
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = null!;

        [JsonPropertyName("description")]
        public string Description { get; set; } = null!;

        [JsonPropertyName("inputSchema")]
        public JsonElement InputSchema { get; set; }

        [JsonPropertyName("annotations")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ToolAnnotations? Annotations { get; set; }
    }

    public class ToolAnnotations
    {
        [JsonPropertyName("title")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Title { get; set; }

        [JsonPropertyName("readOnlyHint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool ReadOnlyHint { get; set; }

        [JsonPropertyName("destructiveHint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool DestructiveHint { get; set; }

        [JsonPropertyName("idempotentHint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IdempotentHint { get; set; }

        [JsonPropertyName("openWorldHint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool OpenWorldHint { get; set; }
    }

    public class ToolCallParams
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = null!;

        [JsonPropertyName("arguments")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? Arguments { get; set; }
    }

    public class ToolResult
    {
        [JsonPropertyName("content")]
        public List<ContentItem> Content { get; set; } = new();

        [JsonPropertyName("isError")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsError { get; set; }

        public static ToolResult Text(string text) => new()
        {
            Content = new List<ContentItem> { ContentItem.FromText(text) }
        };

        public static ToolResult Error(string message) => new()
        {
            Content = new List<ContentItem> { ContentItem.FromText(message) },
            IsError = true
        };
    }

    public class ContentItem
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "text";

        [JsonPropertyName("text")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Text { get; set; }

        [JsonPropertyName("data")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Data { get; set; }

        [JsonPropertyName("mimeType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MimeType { get; set; }

        public static ContentItem FromText(string text) => new() { Type = "text", Text = text };

        public static ContentItem FromImage(string base64, string mimeType) => new()
        {
            Type = "image",
            Data = base64,
            MimeType = mimeType
        };
    }

    public class ToolListResult
    {
        [JsonPropertyName("tools")]
        public List<ToolDefinition> Tools { get; set; } = new();

        [JsonPropertyName("nextCursor")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NextCursor { get; set; }
    }

    public class ResourceDefinition
    {
        [JsonPropertyName("uri")]
        public string Uri { get; set; } = null!;

        [JsonPropertyName("name")]
        public string Name { get; set; } = null!;

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

        [JsonPropertyName("mimeType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MimeType { get; set; }
    }

    public class ResourceListResult
    {
        [JsonPropertyName("resources")]
        public List<ResourceDefinition> Resources { get; set; } = new();
    }

    public class ResourceReadResult
    {
        [JsonPropertyName("contents")]
        public List<ResourceContent> Contents { get; set; } = new();
    }

    public class ResourceContent
    {
        [JsonPropertyName("uri")]
        public string Uri { get; set; } = null!;

        [JsonPropertyName("mimeType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MimeType { get; set; }

        [JsonPropertyName("text")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Text { get; set; }
    }
}
