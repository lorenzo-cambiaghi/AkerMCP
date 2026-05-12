using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MCPSharp.Shared.Protocol;

namespace MCPSharp.Server
{
    public class McpServer
    {
        private readonly StdioTransport _transport;
        private readonly ToolRegistry _toolRegistry;
        private readonly ResourceRegistry _resourceRegistry;
        private readonly JsonSerializerOptions _jsonOptions;

        public McpServer(StdioTransport transport, ToolRegistry toolRegistry, ResourceRegistry resourceRegistry)
        {
            _transport = transport;
            _toolRegistry = toolRegistry;
            _resourceRegistry = resourceRegistry;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            };
        }

        public async Task Run(CancellationToken ct)
        {
            StdioTransport.LogInfo("MCPSharp server starting...");

            while (!ct.IsCancellationRequested)
            {
                var message = await _transport.ReadMessage(ct).ConfigureAwait(false);
                if (message == null) break;

                await HandleMessage(message.Value, ct).ConfigureAwait(false);
            }

            StdioTransport.LogInfo("MCPSharp server shutting down.");
        }

        private async Task HandleMessage(JsonElement message, CancellationToken ct)
        {
            if (!message.TryGetProperty("method", out var methodElement))
            {
                return;
            }

            var method = methodElement.GetString();
            if (method == null) return;

            var hasId = message.TryGetProperty("id", out var idElement);

            if (!hasId)
            {
                await HandleNotification(method, message, ct).ConfigureAwait(false);
                return;
            }

            object id = idElement.ValueKind == JsonValueKind.Number
                ? idElement.GetInt64()
                : (object)(idElement.GetString() ?? "0");

            try
            {
                var result = await HandleRequest(method, message, ct).ConfigureAwait(false);
                var response = JsonRpcResponse.Success(id, result);
                await _transport.SendResponse(response, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var response = JsonRpcResponse.Fail(id, JsonRpcErrorCodes.InternalError, ex.Message);
                await _transport.SendResponse(response, ct).ConfigureAwait(false);
            }
        }

        private async Task<object> HandleRequest(string method, JsonElement message, CancellationToken ct)
        {
            JsonElement? paramsElement = message.TryGetProperty("params", out var p) ? p : (JsonElement?)null;

            switch (method)
            {
                case McpConstants.Methods.Initialize:
                    return HandleInitialize(paramsElement);

                case McpConstants.Methods.Ping:
                    return new { };

                case McpConstants.Methods.ToolsList:
                    return _toolRegistry.ListTools();

                case McpConstants.Methods.ToolsCall:
                    if (paramsElement == null)
                        throw new InvalidOperationException("Missing params for tools/call");
                    return await _toolRegistry.CallTool(paramsElement.Value, ct).ConfigureAwait(false);

                case McpConstants.Methods.ResourcesList:
                    return _resourceRegistry.ListResources();

                case McpConstants.Methods.ResourcesRead:
                    if (paramsElement == null)
                        throw new InvalidOperationException("Missing params for resources/read");
                    return await _resourceRegistry.ReadResource(paramsElement.Value, ct).ConfigureAwait(false);

                default:
                    throw new InvalidOperationException($"Unknown method: {method}");
            }
        }

        private Task HandleNotification(string method, JsonElement message, CancellationToken ct)
        {
            switch (method)
            {
                case McpConstants.Methods.Initialized:
                    StdioTransport.LogInfo("Client initialized successfully.");
                    break;

                default:
                    StdioTransport.LogInfo($"Received notification: {method}");
                    break;
            }
            return Task.CompletedTask;
        }

        private InitializeResult HandleInitialize(JsonElement? paramsElement)
        {
            StdioTransport.LogInfo("Handling initialize request...");

            return new InitializeResult
            {
                ProtocolVersion = McpConstants.ProtocolVersion,
                ServerInfo = new ImplementationInfo
                {
                    Name = "MCPSharp",
                    Version = "1.0.0"
                },
                Capabilities = new ServerCapabilities
                {
                    Tools = new ToolsCapability { ListChanged = true },
                    Resources = new ResourcesCapability { Subscribe = false, ListChanged = true }
                }
            };
        }
    }
}
