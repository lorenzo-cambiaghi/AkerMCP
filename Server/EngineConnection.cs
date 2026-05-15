using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AkerMcp.Shared.Ipc;
using AkerMcp.Shared.Protocol;
using AkerMcp.Shared.Serialization;
using MessagePack;

namespace AkerMcp.Server
{
    public class EngineConnection : IDisposable
    {
        private IpcChannel? _channel;
        private int _nextRequestId;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<IpcResponse>> _pendingRequests
            = new ConcurrentDictionary<int, TaskCompletionSource<IpcResponse>>();
        private readonly GenericSerializer _serializer = new GenericSerializer();
        private CancellationTokenSource? _listenerCts;
        private bool _disposed;

        public bool IsConnected => _channel != null;

        public async Task<bool> TryConnect(string pipeName, int timeoutMs = 5000, CancellationToken ct = default)
        {
            try
            {
                var client = new NamedPipeClientStream(".", pipeName,
                    PipeDirection.InOut, PipeOptions.Asynchronous);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);

                await client.ConnectAsync(cts.Token).ConfigureAwait(false);
                _channel = new IpcChannel(client);

                _listenerCts = new CancellationTokenSource();
                _ = Task.Run(() => ListenForResponses(_listenerCts.Token));

                StdioTransport.LogInfo($"Connected to engine plugin via pipe: {pipeName}");
                return true;
            }
            catch (Exception ex)
            {
                StdioTransport.LogError($"Failed to connect to engine: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> TryDiscoverAndConnect(CancellationToken ct = default)
        {
            var discoveryDir = Path.Combine(Path.GetTempPath(), IpcConstants.DiscoveryDirectory);
            if (!Directory.Exists(discoveryDir))
            {
                StdioTransport.LogInfo("No engine plugin discovered (discovery directory not found).");
                return false;
            }

            PurgeStaleLockFiles(discoveryDir);

            foreach (var lockFile in Directory.GetFiles(discoveryDir, "*.lock"))
            {
                try
                {
                    var content = File.ReadAllText(lockFile);
                    var info = JsonSerializer.Deserialize<JsonElement>(content);

                    if (!info.TryGetProperty("pipe", out var pipeElement))
                        continue;

                    var pipeName = pipeElement.GetString();
                    if (pipeName == null)
                        continue;

                    StdioTransport.LogInfo($"Attempting connection to pipe: {pipeName}");

                    if (await TryConnect(pipeName, 5000, ct).ConfigureAwait(false))
                    {
                        var engineName = info.TryGetProperty("engine", out var eng) ? eng.GetString() : "Unknown";
                        var clientProtocol = info.TryGetProperty("protocolVersion", out var prot) ? prot.GetString() : "Unknown";
                        
                        StdioTransport.LogInfo($"Connected to {engineName} engine via {pipeName} (Client Protocol: v{clientProtocol})");
                        
                        if (clientProtocol != IpcConstants.ProtocolVersion)
                        {
                            StdioTransport.LogError($"PROTOCOL MISMATCH! Server is v{IpcConstants.ProtocolVersion} but Client is v{clientProtocol}. Expect connection errors.");
                        }
                        
                        return true;
                    }
                }
                catch
                {
                    // Corrupt lock file, skip
                }
            }

            StdioTransport.LogInfo("No engine plugin available after discovery scan.");
            return false;
        }

        private static void PurgeStaleLockFiles(string discoveryDir)
        {
            foreach (var lockFile in Directory.GetFiles(discoveryDir, "*.lock"))
            {
                try
                {
                    var content = File.ReadAllText(lockFile);
                    var info = JsonSerializer.Deserialize<JsonElement>(content);

                    if (info.TryGetProperty("pid", out var pidElement))
                    {
                        var pid = pidElement.GetInt32();
                        if (!IsProcessAlive(pid))
                        {
                            StdioTransport.LogInfo($"Removing stale lock file: {Path.GetFileName(lockFile)} (PID {pid} is dead)");
                            File.Delete(lockFile);
                        }
                    }
                }
                catch
                {
                    // Corrupt file, remove it
                    try { File.Delete(lockFile); } catch { }
                }
            }
        }

        private static bool IsProcessAlive(int pid)
        {
            try
            {
                var process = System.Diagnostics.Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        public async Task<ToolResult> ForwardToolCall(string method, JsonElement? arguments, CancellationToken ct)
        {
            if (_channel == null)
                return ToolResult.Error("No engine connected. Start the engine plugin first.");

            var payload = arguments.HasValue
                ? System.Text.Encoding.UTF8.GetBytes(arguments.Value.GetRawText())
                : null;

            var requestId = Interlocked.Increment(ref _nextRequestId);
            var request = new IpcRequest
            {
                Id = requestId,
                Method = method,
                Payload = payload
            };

            var tcs = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[requestId] = tcs;

            try
            {
                await _channel.SendRequest(request, ct).ConfigureAwait(false);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(30000);
                using var reg = cts.Token.Register(() => tcs.TrySetCanceled());

                var response = await tcs.Task.ConfigureAwait(false);

                if (!response.Success)
                    return ToolResult.Error(response.Error ?? "Unknown engine error");

                var resultText = response.Payload != null
                    ? System.Text.Encoding.UTF8.GetString(response.Payload)
                    : "OK";

                return ToolResult.Text(resultText);
            }
            finally
            {
                _pendingRequests.TryRemove(requestId, out _);
            }
        }

        public async Task<BinaryToolCallResult> ForwardBinaryToolCall(
            string method, JsonElement? arguments, CancellationToken ct)
        {
            if (_channel == null)
                return new BinaryToolCallResult { Error = "No engine connected." };

            var payload = arguments.HasValue
                ? System.Text.Encoding.UTF8.GetBytes(arguments.Value.GetRawText())
                : null;

            var requestId = Interlocked.Increment(ref _nextRequestId);
            var request = new IpcRequest
            {
                Id = requestId,
                Method = method,
                Payload = payload
            };

            var tcs = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[requestId] = tcs;

            try
            {
                await _channel.SendRequest(request, ct).ConfigureAwait(false);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(30000);
                using var reg = cts.Token.Register(() => tcs.TrySetCanceled());

                var response = await tcs.Task.ConfigureAwait(false);

                if (!response.Success)
                {
                    return new BinaryToolCallResult
                    {
                        ErrorCode = response.ErrorCode,
                        Error = response.Error
                    };
                }

                return new BinaryToolCallResult
                {
                    Bytes = response.Payload,
                    ContentType = response.ContentType
                };
            }
            finally
            {
                _pendingRequests.TryRemove(requestId, out _);
            }
        }

        public async Task<string> ForwardResourceRead(string method, CancellationToken ct)
        {
            if (_channel == null)
                return "(No engine connected)";

            var requestId = Interlocked.Increment(ref _nextRequestId);
            var request = new IpcRequest
            {
                Id = requestId,
                Method = method
            };

            var tcs = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[requestId] = tcs;

            try
            {
                await _channel.SendRequest(request, ct).ConfigureAwait(false);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(10000);
                using var reg = cts.Token.Register(() => tcs.TrySetCanceled());

                var response = await tcs.Task.ConfigureAwait(false);

                if (!response.Success)
                    return $"Error: {response.Error}";

                return response.Payload != null
                    ? System.Text.Encoding.UTF8.GetString(response.Payload)
                    : "";
            }
            finally
            {
                _pendingRequests.TryRemove(requestId, out _);
            }
        }

        private async Task ListenForResponses(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _channel != null)
                {
                    var response = await _channel.ReceiveResponse(ct).ConfigureAwait(false);
                    if (_pendingRequests.TryGetValue(response.Id, out var tcs))
                    {
                        tcs.TrySetResult(response);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (EndOfStreamException)
            {
                StdioTransport.LogInfo("Engine plugin disconnected gracefully.");
                CancelPendingRequests();
            }
            catch (Exception ex)
            {
                StdioTransport.LogInfo($"Engine connection lost: {ex.Message}");
                CancelPendingRequests();
            }
            finally
            {
                Disconnect();
            }
        }

        private void CancelPendingRequests()
        {
            foreach (var pending in _pendingRequests.Values)
                pending.TrySetCanceled();
            _pendingRequests.Clear();
        }

        private void Disconnect()
        {
            _channel?.Dispose();
            _channel = null; // This allows IsConnected to become false, triggering the retry loop
        }

        public class BinaryToolCallResult
        {
            public byte[]? Bytes { get; set; }
            public string? ContentType { get; set; }
            public string? ErrorCode { get; set; }
            public string? Error { get; set; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _listenerCts?.Cancel();
            _listenerCts?.Dispose();
            _channel?.Dispose();
        }
    }
}
