using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AkerMcp.Shared.Protocol;

namespace AkerMcp.Server
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
            StdioTransport.LogInfo("AkerMcp server starting...");

            while (!ct.IsCancellationRequested)
            {
                var message = await _transport.ReadMessage(ct).ConfigureAwait(false);
                if (message == null) break;

                await HandleMessage(message.Value, ct).ConfigureAwait(false);
            }

            StdioTransport.LogInfo("AkerMcp server shutting down.");
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
            catch (MethodNotFoundException ex)
            {
                var response = JsonRpcResponse.Fail(id, JsonRpcErrorCodes.MethodNotFound, ex.Message);
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
                    throw new MethodNotFoundException(method);
            }
        }

        private sealed class MethodNotFoundException : InvalidOperationException
        {
            public MethodNotFoundException(string method) : base($"Unknown method: {method}") { }
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

        // Wire-compatible protocol revisions this server can speak. The first
        // entry is the default offered when the client's request is unsupported.
        private static readonly string[] SupportedProtocolVersions =
        {
            McpConstants.ProtocolVersion,
            "2024-11-05"
        };

        private InitializeResult HandleInitialize(JsonElement? paramsElement)
        {
            StdioTransport.LogInfo("Handling initialize request...");

            // Per MCP spec: echo the client's requested version when supported,
            // otherwise respond with the latest version we support.
            string? requested = null;
            if (paramsElement.HasValue &&
                paramsElement.Value.TryGetProperty("protocolVersion", out var pv))
                requested = pv.GetString();

            var negotiated = requested != null && Array.IndexOf(SupportedProtocolVersions, requested) >= 0
                ? requested
                : McpConstants.ProtocolVersion;

            return new InitializeResult
            {
                ProtocolVersion = negotiated,
                ServerInfo = new ImplementationInfo
                {
                    Name = "AkerMcp",
                    Version = "1.0.0"
                },
                Instructions = ServerInstructions.Handshake(
                    _toolRegistry.ToolNames, _toolRegistry.HiddenTools, _toolRegistry.Profile),
                Capabilities = new ServerCapabilities
                {
                    // We never emit list_changed notifications, so don't advertise them.
                    Tools = new ToolsCapability { ListChanged = false },
                    Resources = new ResourcesCapability { Subscribe = false, ListChanged = false }
                }
            };
        }
    }
}
