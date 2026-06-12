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

        // Heartbeat: catches "zombie" connections where the pipe is technically open
        // but the engine main thread is frozen (no responses ever come back).
        private const int HeartbeatIntervalMs = 10_000;
        private const int HeartbeatTimeoutMs = 5_000;

        // The engine plugin restarts itself after every Unity domain reload (script
        // recompilation) and the background retry loop reconnects within ~2-10s.
        // Tool calls arriving in that window wait instead of failing immediately.
        private const int ReconnectGraceMs = 20_000;
        private const int DefaultRequestTimeoutMs = 30_000;

        // Marker prefix so callers (e.g. the refresh_scripts orchestration) can
        // distinguish "connection dropped mid-call" from ordinary tool errors.
        public const string EngineDisconnectedPrefix = "[ENGINE_DISCONNECTED]";

        public bool IsConnected => _channel != null;

        /// <summary>Polls until the background retry loop re-establishes the connection.</summary>
        public async Task<bool> WaitForConnection(int timeoutMs, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                if (IsConnected) return true;
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            return IsConnected;
        }

        public async Task<bool> TryConnect(string pipeName, int timeoutMs = 5000, CancellationToken ct = default)
        {
            NamedPipeClientStream? client = null;
            try
            {
                client = new NamedPipeClientStream(".", pipeName,
                    PipeDirection.InOut, PipeOptions.Asynchronous);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);

                await client.ConnectAsync(cts.Token).ConfigureAwait(false);

                // Disconnect() uses Interlocked.Exchange and nulls these fields, so
                // a previous connection's cleanup never races with this assignment.
                var newCts = new CancellationTokenSource();
                var newChannel = new IpcChannel(client);
                client = null; // ownership transferred to IpcChannel; do not dispose in catch
                _listenerCts = newCts;
                _channel = newChannel;

                // Pass the channel by value so each task is bound to ITS connection.
                // Even if the field is later swapped to a new channel, an old task
                // can never accidentally read from / write to it.
                var listenerToken = newCts.Token;
                _ = Task.Run(() => ListenForResponses(newChannel, listenerToken));
                _ = Task.Run(() => RunHeartbeat(newChannel, listenerToken));

                StdioTransport.LogInfo($"Connected to engine plugin via pipe: {pipeName}");
                return true;
            }
            catch (Exception ex)
            {
                // Avoid leaking the pipe handle on failed ConnectAsync.
                try { client?.Dispose(); } catch { }
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

                    if (!IsLockOwnerAlive(info, out var reason))
                    {
                        StdioTransport.LogInfo($"Removing stale lock file: {Path.GetFileName(lockFile)} ({reason})");
                        File.Delete(lockFile);
                    }
                }
                catch
                {
                    // Corrupt file, remove it
                    try { File.Delete(lockFile); } catch { }
                }
            }
        }

        private static bool IsLockOwnerAlive(JsonElement info, out string reason)
        {
            reason = "unknown";
            if (!info.TryGetProperty("pid", out var pidElement))
            {
                reason = "missing pid field";
                return false;
            }

            var pid = pidElement.GetInt32();
            System.Diagnostics.Process process;
            try { process = System.Diagnostics.Process.GetProcessById(pid); }
            catch
            {
                reason = $"PID {pid} is dead";
                return false;
            }

            try
            {
                if (process.HasExited)
                {
                    reason = $"PID {pid} has exited";
                    return false;
                }

                // Guard against PID recycling: compare process start time with the one
                // recorded in the lock file. Tolerate small clock-skew (2s).
                if (info.TryGetProperty("startTime", out var startElement) &&
                    DateTime.TryParse(startElement.GetString(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var lockStart))
                {
                    var processStart = process.StartTime.ToUniversalTime();
                    if (System.Math.Abs((lockStart - processStart).TotalSeconds) > 2)
                    {
                        reason = $"PID {pid} was recycled (start time mismatch)";
                        return false;
                    }
                }
                return true;
            }
            finally
            {
                process.Dispose();
            }
        }

        public async Task<ToolResult> ForwardToolCall(string method, JsonElement? arguments, CancellationToken ct,
            int timeoutMs = DefaultRequestTimeoutMs)
        {
            var channel = _channel;
            if (channel == null)
            {
                // Most common cause: Unity is mid domain-reload. Give the retry
                // loop a chance instead of failing the call (and burning a
                // model round-trip on a transient state).
                if (!await WaitForConnection(ReconnectGraceMs, ct).ConfigureAwait(false))
                    return ToolResult.Error(
                        $"{EngineDisconnectedPrefix} No engine connected (waited {ReconnectGraceMs / 1000}s). " +
                        "If Unity is compiling, retry shortly; otherwise check that the AkerMcp plugin is running in the editor.");
                channel = _channel;
                if (channel == null)
                    return ToolResult.Error($"{EngineDisconnectedPrefix} Engine connection dropped again — retry shortly.");
            }

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
                await channel.SendRequest(request, ct).ConfigureAwait(false);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);
                using var reg = cts.Token.Register(() => tcs.TrySetCanceled());

                var response = await tcs.Task.ConfigureAwait(false);

                if (!response.Success)
                    return ToolResult.Error(response.Error ?? "Unknown engine error");

                var resultText = response.Payload != null
                    ? System.Text.Encoding.UTF8.GetString(response.Payload)
                    : "OK";

                return ToolResult.Text(resultText);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // caller cancelled — propagate
            }
            catch (OperationCanceledException)
            {
                // Either the per-call timeout fired or Disconnect() cancelled the
                // pending request. Disambiguate so the model gets an actionable error.
                if (!IsConnected)
                    return ToolResult.Error(
                        $"{EngineDisconnectedPrefix} Engine disconnected while '{method}' was in flight " +
                        "(usually a Unity domain reload after script recompilation). It reconnects automatically — retry shortly.");
                return ToolResult.Error($"Tool '{method}' timed out after {timeoutMs / 1000}s (engine still connected).");
            }
            catch (IOException ex)
            {
                return ToolResult.Error($"{EngineDisconnectedPrefix} Pipe error during '{method}': {ex.Message}. Retry shortly.");
            }
            finally
            {
                _pendingRequests.TryRemove(requestId, out _);
            }
        }

        public async Task<BinaryToolCallResult> ForwardBinaryToolCall(
            string method, JsonElement? arguments, CancellationToken ct)
        {
            var channel = _channel;
            if (channel == null)
            {
                if (!await WaitForConnection(ReconnectGraceMs, ct).ConfigureAwait(false))
                    return new BinaryToolCallResult { Error = "No engine connected. If Unity is compiling, retry shortly." };
                channel = _channel;
                if (channel == null)
                    return new BinaryToolCallResult { Error = "Engine connection dropped — retry shortly." };
            }

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
                await channel.SendRequest(request, ct).ConfigureAwait(false);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(DefaultRequestTimeoutMs);
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // caller cancelled — propagate
            }
            catch (OperationCanceledException)
            {
                if (!IsConnected)
                    return new BinaryToolCallResult
                    {
                        Error = $"{EngineDisconnectedPrefix} Engine disconnected while '{method}' was in flight " +
                                "(usually a Unity domain reload after script recompilation). It reconnects automatically — retry shortly."
                    };
                return new BinaryToolCallResult
                {
                    Error = $"'{method}' timed out after {DefaultRequestTimeoutMs / 1000}s (engine still connected)."
                };
            }
            catch (IOException ex)
            {
                return new BinaryToolCallResult
                {
                    Error = $"{EngineDisconnectedPrefix} Pipe error during '{method}': {ex.Message}. Retry shortly."
                };
            }
            finally
            {
                _pendingRequests.TryRemove(requestId, out _);
            }
        }

        public async Task<string> ForwardResourceRead(string method, CancellationToken ct)
        {
            var channel = _channel;
            if (channel == null)
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
                await channel.SendRequest(request, ct).ConfigureAwait(false);

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

        private async Task ListenForResponses(IpcChannel channel, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var response = await channel.ReceiveResponse(ct).ConfigureAwait(false);
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
            }
            catch (ObjectDisposedException)
            {
                // Channel was disposed by Disconnect() racing on another thread.
            }
            catch (Exception ex)
            {
                StdioTransport.LogInfo($"Engine connection lost: {ex.Message}");
            }
            finally
            {
                Disconnect();
            }
        }

        private async Task RunHeartbeat(IpcChannel channel, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(HeartbeatIntervalMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                var pingId = Interlocked.Increment(ref _nextRequestId);
                var tcs = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingRequests[pingId] = tcs;

                try
                {
                    await channel.SendRequest(new IpcRequest
                    {
                        Id = pingId,
                        Method = IpcConstants.Methods.Ping
                    }, ct).ConfigureAwait(false);

                    using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    pingCts.CancelAfter(HeartbeatTimeoutMs);
                    using var reg = pingCts.Token.Register(() => tcs.TrySetCanceled());
                    await tcs.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    StdioTransport.LogInfo($"Heartbeat failed — dropping zombie connection: {ex.GetType().Name}: {ex.Message}");
                    Disconnect();
                    return;
                }
                finally
                {
                    _pendingRequests.TryRemove(pingId, out _);
                }
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
            // Atomic claim: exactly one caller wins each pair, so a stale Disconnect
            // (e.g. listener finally running after heartbeat already tore down)
            // cannot accidentally cancel a freshly-created next connection.
            var oldCts = Interlocked.Exchange(ref _listenerCts, null);
            var oldChannel = Interlocked.Exchange(ref _channel, null);

            try { oldCts?.Cancel(); } catch { }
            try { oldChannel?.Dispose(); } catch { }
            try { oldCts?.Dispose(); } catch { }

            CancelPendingRequests();
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
            Disconnect();
        }
    }
}
