using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AkerMcp.Shared.Protocol;

namespace AkerMcp.Server
{
    /// <summary>A request that is answered with a specific JSON-RPC error code instead of -32603.</summary>
    public sealed class McpRequestException : Exception
    {
        public McpRequestException(int code, string message) : base(message) { Code = code; }
        public int Code { get; }
    }

    public class McpServer
    {
        private readonly StdioTransport _transport;
        private readonly ToolRegistry _toolRegistry;
        private readonly ResourceRegistry _resourceRegistry;

        // stdin is read all the time, and only the work that touches the engine waits its turn.
        // Before, the loop awaited each call before reading the next line: a ping went unanswered
        // behind a build_player (deadline 30 minutes), and a notifications/cancelled was read only
        // once the call it cancelled had already ended. Tool calls stay one at a time because
        // ToolRegistry keeps per-session state (the last capture, the game window) and two input
        // sequences must never interleave.
        private readonly SemaphoreSlim _executionGate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight =
            new ConcurrentDictionary<string, CancellationTokenSource>();
        private readonly ConcurrentDictionary<Task, bool> _running = new ConcurrentDictionary<Task, bool>();

        public McpServer(StdioTransport transport, ToolRegistry toolRegistry, ResourceRegistry resourceRegistry)
        {
            _transport = transport;
            _toolRegistry = toolRegistry;
            _resourceRegistry = resourceRegistry;
        }

        public async Task Run(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var incoming = await _transport.ReadMessage(ct).ConfigureAwait(false);
                if (incoming.Ended) break;

                if (incoming.ParseError != null)
                {
                    await Send(JsonRpcResponse.Fail(null, JsonRpcErrorCodes.ParseError,
                        $"Parse error: {incoming.ParseError}"), ct).ConfigureAwait(false);
                    continue;
                }

                var message = incoming.Message;
                if (message.ValueKind == JsonValueKind.Array)
                {
                    Track(HandleBatch(message, ct));
                    continue;
                }

                // Notifications are handled here, in order: a cancellation has to reach the call it
                // names now, not after it.
                var request = Classify(message, out var invalid);
                if (invalid != null)
                {
                    await Send(invalid, ct).ConfigureAwait(false);
                    continue;
                }
                if (request.IsNotification)
                {
                    HandleNotification(request.Method, message);
                    continue;
                }

                Track(AnswerAsync(request, message, ct));
            }

            // Nobody is left to read the answers: stop what is still running.
            foreach (var pending in _inFlight.Values)
            {
                try { pending.Cancel(); } catch (ObjectDisposedException) { }
            }
            try { await Task.WhenAll(_running.Keys).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch { /* the process is exiting; a call that did not stop in time is abandoned */ }

            StdioTransport.LogInfo("AkerMcp server shutting down.");
        }

        private void Track(Task task)
        {
            _running[task] = true;
            _ = task.ContinueWith(t => _running.TryRemove(t, out _), TaskScheduler.Default);
        }

        private async Task AnswerAsync(Request request, JsonElement message, CancellationToken ct)
        {
            await Task.Yield(); // off the reading loop
            var response = await HandleRequest(request, message, ct).ConfigureAwait(false);
            if (response != null) await Send(response, ct).ConfigureAwait(false);
        }

        private async Task HandleBatch(JsonElement batch, CancellationToken ct)
        {
            await Task.Yield();
            if (batch.GetArrayLength() == 0)
            {
                await Send(JsonRpcResponse.Fail(null, JsonRpcErrorCodes.InvalidRequest,
                    "Invalid Request: an empty batch."), ct).ConfigureAwait(false);
                return;
            }

            var responses = new List<JsonRpcResponse>();
            foreach (var element in batch.EnumerateArray())
            {
                var request = Classify(element, out var invalid);
                if (invalid != null) { responses.Add(invalid); continue; }
                if (request.IsNotification) { HandleNotification(request.Method, element); continue; }

                var response = await HandleRequest(request, element, ct).ConfigureAwait(false);
                if (response != null) responses.Add(response);
            }

            // A batch of notifications only is answered with nothing at all.
            if (responses.Count > 0)
                await SendBatch(responses.ToArray(), ct).ConfigureAwait(false);
        }

        private readonly struct Request
        {
            public Request(string method, object? id, bool isNotification)
            {
                Method = method;
                Id = id;
                IsNotification = isNotification;
            }

            public string Method { get; }
            public object? Id { get; }
            public bool IsNotification { get; }
        }

        /// <summary>
        /// Reads method and id, or the error that says why they cannot be read. Each of these used to throw
        /// out of the loop and end the process: a message that is not an object, a method that is not a string,
        /// an id of 1.5, true or 1e20.
        /// </summary>
        private static Request Classify(JsonElement message, out JsonRpcResponse? invalid)
        {
            invalid = null;
            if (message.ValueKind != JsonValueKind.Object)
            {
                invalid = JsonRpcResponse.Fail(null, JsonRpcErrorCodes.InvalidRequest,
                    "Invalid Request: a JSON-RPC message is an object.");
                return default;
            }

            object? id = null;
            bool hasId = message.TryGetProperty("id", out var idElement);
            if (hasId)
            {
                if (idElement.ValueKind == JsonValueKind.String) id = idElement.GetString();
                else if (idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt64(out var n)) id = n;
                else
                {
                    invalid = JsonRpcResponse.Fail(null, JsonRpcErrorCodes.InvalidRequest,
                        $"Invalid Request: the id must be a string or an integer, not {idElement.GetRawText()}.");
                    return default;
                }
            }

            if (!message.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String ||
                methodElement.GetString() is not { Length: > 0 } method)
            {
                // A message with an id and no method is a response to us. This server sends no requests,
                // so there is nothing to match it with, and answering a response is not allowed.
                if (hasId && !message.TryGetProperty("method", out _) &&
                    (message.TryGetProperty("result", out _) || message.TryGetProperty("error", out _)))
                    return new Request("", id, isNotification: true);

                invalid = JsonRpcResponse.Fail(hasId ? id : null, JsonRpcErrorCodes.InvalidRequest,
                    "Invalid Request: 'method' must be a non-empty string.");
                return default;
            }

            return new Request(method, id, isNotification: !hasId);
        }

        private static string IdKey(object? id) => id switch
        {
            long n => "n:" + n,
            string s => "s:" + s,
            _ => "?"
        };

        private async Task<JsonRpcResponse?> HandleRequest(Request request, JsonElement message, CancellationToken ct)
        {
            var key = IdKey(request.Id);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (!_inFlight.TryAdd(key, cts))
                return JsonRpcResponse.Fail(request.Id, JsonRpcErrorCodes.InvalidRequest,
                    $"Invalid Request: id {request.Id} is already in use by a request still running.");

            try
            {
                var result = await Dispatch(request.Method, message, cts.Token).ConfigureAwait(false);
                return JsonRpcResponse.Success(request.Id, result);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Cancelled by the client (or the server is going away): MCP says not to answer.
                return null;
            }
            catch (McpRequestException ex)
            {
                return JsonRpcResponse.Fail(request.Id, ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                return JsonRpcResponse.Fail(request.Id, JsonRpcErrorCodes.InternalError, ex.Message);
            }
            finally
            {
                _inFlight.TryRemove(key, out _);
            }
        }

        private async Task<object> Dispatch(string method, JsonElement message, CancellationToken ct)
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
                    if (paramsElement is not { ValueKind: JsonValueKind.Object } callParams)
                        throw new McpRequestException(JsonRpcErrorCodes.InvalidParams, "Missing params for tools/call");
                    return await Exclusive(() => _toolRegistry.CallTool(callParams, ct), ct).ConfigureAwait(false);

                case McpConstants.Methods.ResourcesList:
                    return _resourceRegistry.ListResources();

                case McpConstants.Methods.ResourcesRead:
                    if (paramsElement is not { ValueKind: JsonValueKind.Object } readParams)
                        throw new McpRequestException(JsonRpcErrorCodes.InvalidParams, "Missing params for resources/read");
                    return await Exclusive(() => _resourceRegistry.ReadResource(readParams, ct), ct).ConfigureAwait(false);

                default:
                    throw new McpRequestException(JsonRpcErrorCodes.MethodNotFound, $"Unknown method: {method}");
            }
        }

        /// <summary>Runs work that reaches the engine, one call at a time. A call cancelled while it waits never starts.</summary>
        private async Task<T> Exclusive<T>(Func<Task<T>> work, CancellationToken ct)
        {
            await _executionGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await work().ConfigureAwait(false);
            }
            finally
            {
                _executionGate.Release();
            }
        }

        private void HandleNotification(string method, JsonElement message)
        {
            switch (method)
            {
                case "":
                    break; // a response to a request this server never sent

                case McpConstants.Methods.Initialized:
                    StdioTransport.LogInfo("Client initialized successfully.");
                    break;

                case McpConstants.Methods.Cancelled:
                    if (message.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object &&
                        p.TryGetProperty("requestId", out var rid))
                    {
                        object? id = rid.ValueKind == JsonValueKind.String ? rid.GetString()
                            : rid.ValueKind == JsonValueKind.Number && rid.TryGetInt64(out var n) ? n : (object?)null;
                        if (_inFlight.TryGetValue(IdKey(id), out var cts))
                        {
                            var reason = p.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                                ? $": {r.GetString()}" : "";
                            StdioTransport.LogInfo($"Request {id} cancelled by the client{reason}");
                            try { cts.Cancel(); } catch (ObjectDisposedException) { /* it has just finished */ }
                        }
                    }
                    break;

                default:
                    StdioTransport.LogInfo($"Received notification: {method}");
                    break;
            }
        }

        private async Task Send(JsonRpcResponse response, CancellationToken ct)
        {
            try { await _transport.SendResponse(response, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is OperationCanceledException)
            {
                StdioTransport.LogError($"Could not write the response to {response.Id}: {ex.Message}");
            }
        }

        private async Task SendBatch(JsonRpcResponse[] responses, CancellationToken ct)
        {
            try { await _transport.SendBatch(responses, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is OperationCanceledException)
            {
                StdioTransport.LogError($"Could not write a batch of {responses.Length} responses: {ex.Message}");
            }
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
            if (paramsElement is { ValueKind: JsonValueKind.Object } initParams &&
                initParams.TryGetProperty("protocolVersion", out var pv) &&
                pv.ValueKind == JsonValueKind.String)
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
                    // The product version, from AkerMcp.Server.csproj; a hard-coded
                    // "1.0.0" sat here through six releases. The IPC protocol
                    // version is a different number, see ServerVersion.
                    Version = ServerVersion.Product
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
