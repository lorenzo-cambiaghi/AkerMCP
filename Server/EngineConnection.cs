using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AkerMcp.Shared.Ipc;
using AkerMcp.Shared.Protocol;

namespace AkerMcp.Server
{
    public class EngineConnection : IDisposable
    {
        /// <summary>
        /// One established pipe and everything that belongs to it. Held in a single reference, so "connected" and
        /// "connected to whom" can never disagree, and so a task can only ever tear down the connection it was
        /// started for: a listener or a heartbeat that outlives its pipe used to call a Disconnect() that closed
        /// whichever connection was current — by then, possibly the next one.
        /// </summary>
        private sealed class Connection
        {
            private int _closed;

            public Connection(IpcChannel channel, EngineIdentity engine)
            {
                Channel = channel;
                Engine = engine;
            }

            public IpcChannel Channel { get; }
            public EngineIdentity Engine { get; }
            public CancellationTokenSource Lifetime { get; } = new CancellationTokenSource();
            public ConcurrentDictionary<int, TaskCompletionSource<IpcResponse>> Pending { get; } =
                new ConcurrentDictionary<int, TaskCompletionSource<IpcResponse>>();

            public bool IsClosed => Volatile.Read(ref _closed) != 0;

            /// <summary>Idempotent. The token source is cancelled, not disposed: the tasks bound to it still read it.</summary>
            public void Close()
            {
                if (Interlocked.Exchange(ref _closed, 1) != 0) return;
                try { Lifetime.Cancel(); } catch { }
                try { Channel.Dispose(); } catch { }
                foreach (var pending in Pending.Values)
                    pending.TrySetCanceled();
                Pending.Clear();
            }
        }

        private Connection? _connection;
        private int _nextRequestId;
        private bool _disposed;

        // One connection attempt at a time. The background retry loop and engine_status could both find the server
        // disconnected and both connect: one pipe leaked, with an orphaned listener and heartbeat of its own.
        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);

        // Heartbeat: catches "zombie" connections where the pipe is technically open
        // but the engine main thread is frozen (no responses ever come back).
        private const int HeartbeatIntervalMs = 10_000;
        private const int HeartbeatTimeoutMs = 5_000;

        // The engine plugin restarts itself after every Unity domain reload (script
        // recompilation) and the background retry loop reconnects within ~2-10s.
        // Tool calls arriving in that window wait instead of failing immediately.
        private const int ReconnectGraceMs = 20_000;

        private const int ResourceReadTimeoutMs = 10_000;

        /// <summary>
        /// How long a forwarded call waits for the engine's answer when the tool brings no timeout of its own. A
        /// property rather than a constant so the loopback bench can shorten it: proving that a longer request survives
        /// must not cost thirty real seconds.
        /// </summary>
        public int DefaultRequestTimeoutMs { get; set; } = 30_000;

        // What a tool with its own timeout_ms gets on top of it, to marshal the answer back through the pipe.
        private const int ReplyMarginMs = 5_000;

        /// <summary>
        /// The deadline for a call whose arguments carry <c>timeout_ms</c> (execute): that timeout plus a margin for
        /// the reply, never below the default. With the default alone, an execute asked to wait two minutes was cut at
        /// thirty seconds — "timed out after 30s (engine still connected)" while the engine kept working, and its
        /// answer was lost.
        /// </summary>
        public int RequestTimeoutFor(JsonElement? arguments) =>
            arguments is { ValueKind: JsonValueKind.Object } args
            && args.TryGetProperty("timeout_ms", out var requested)
            && requested.ValueKind == JsonValueKind.Number
            && requested.TryGetInt32(out int ms) && ms > 0
                // In long: near int.MaxValue the margin wrapped the sum negative, and the deadline fell back to the default.
                ? (int)Math.Max(DefaultRequestTimeoutMs, Math.Min((long)ms + ReplyMarginMs, int.MaxValue))
                : DefaultRequestTimeoutMs;

        // Marker prefix so callers (e.g. the refresh_scripts orchestration) can
        // distinguish "connection dropped mid-call" from ordinary tool errors.
        public const string EngineDisconnectedPrefix = "[ENGINE_DISCONNECTED]";

        private Connection? Current => Volatile.Read(ref _connection);

        public bool IsConnected => Current != null;

        /// <summary>Who answered: engine, version, pid, pipe, project. Null when nothing is connected.</summary>
        public EngineIdentity? ConnectedEngine => Current?.Engine;

        /// <summary>
        /// What this server is pinned to, or null for "whoever answers first": an engine name ("unity"), a process
        /// id ("12345") or a project name ("MyGame"), all case-insensitive. Seeded from <c>AKER_MCP_ENGINE</c> and
        /// settable at runtime through the <c>engine_status</c> tool.
        /// <para>
        /// ⚠️ Pinning exists because the alternative is silent: with two editors running, the losing
        /// one is not refused — it is simply never chosen, and the caller has no way to tell. Worse,
        /// the choice can CHANGE mid-session, because a Unity domain reload drops the pipe and the
        /// reconnect starts the scan over. Observed in the field: after a script recompile the server
        /// switched to an animation editor that happened to sort first, and every `execute` kept
        /// compiling — inside the other process, where the engine's own types do not exist. A pin by
        /// engine name alone cannot choose between two editors of the same engine; pid and project can.
        /// </para>
        /// </summary>
        public string? PinnedEngine { get; set; } =
            Environment.GetEnvironmentVariable("AKER_MCP_ENGINE") is { Length: > 0 } pinned ? pinned : null;

        /// <summary>An engine plugin that has announced itself in the discovery directory.</summary>
        public sealed class EngineIdentity
        {
            public string Engine { get; set; } = "Unknown";
            public string Version { get; set; } = "";
            public string Pipe { get; set; } = "";
            public int Pid { get; set; }
            public string ProtocolVersion { get; set; } = "";
            public string? Project { get; set; }
            public string? ProjectPath { get; set; }

            /// <summary>Whether a pin names this engine: by engine name, by pid, or by project name.</summary>
            public bool Is(string? pin)
            {
                if (string.IsNullOrWhiteSpace(pin)) return false;
                var p = pin!.Trim();
                return string.Equals(Engine, p, StringComparison.OrdinalIgnoreCase)
                    || (int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid == Pid)
                    || (Project != null && string.Equals(Project, p, StringComparison.OrdinalIgnoreCase));
            }

            public override string ToString()
                => Project != null ? $"{Engine} {Version} (pid {Pid}, project {Project})" : $"{Engine} {Version} (pid {Pid})";
        }

        /// <summary>
        /// Everyone who has announced themselves and is still alive. Reading it is cheap and has no
        /// side effect on the current connection — it is what <c>engine_status</c> reports.
        /// </summary>
        public static List<EngineIdentity> DiscoverEngines()
        {
            var found = LockFileStore.ReadLive(StdioTransport.LogInfo).ConvertAll(info => new EngineIdentity
            {
                Pipe = info.Pipe,
                Engine = string.IsNullOrEmpty(info.Engine) ? "Unknown" : info.Engine,
                Version = info.Version ?? "",
                Pid = info.Pid,
                ProtocolVersion = info.ProtocolVersion ?? "",
                Project = string.IsNullOrWhiteSpace(info.Project) ? null : info.Project,
                ProjectPath = string.IsNullOrWhiteSpace(info.ProjectPath) ? null : info.ProjectPath,
            });

            // Stable order, and NOT the filesystem's: "whoever sorts first alphabetically" is an
            // accident of naming, and it silently decided which editor an agent was talking to.
            // The pipe name breaks ties so the order is total even for two plugins in one process.
            found.Sort((a, b) => a.Pid != b.Pid
                ? a.Pid.CompareTo(b.Pid)
                : string.CompareOrdinal(a.Pipe, b.Pipe));
            return found;
        }

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

        private async Task<Connection?> Open(EngineIdentity candidate, int timeoutMs, CancellationToken ct)
        {
            NamedPipeClientStream? client = null;
            try
            {
                client = new NamedPipeClientStream(".", candidate.Pipe,
                    PipeDirection.InOut, PipeOptions.Asynchronous);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);

                await client.ConnectAsync(cts.Token).ConfigureAwait(false);

                var connection = new Connection(new IpcChannel(client), candidate);
                client = null; // ownership transferred to IpcChannel; do not dispose in catch
                StdioTransport.LogInfo($"Connected to engine plugin via pipe: {candidate.Pipe}");
                return connection;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                try { client?.Dispose(); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                // Avoid leaking the pipe handle on failed ConnectAsync.
                try { client?.Dispose(); } catch { }
                StdioTransport.LogError($"Failed to connect to engine: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> TryDiscoverAndConnect(CancellationToken ct = default)
        {
            await _connectLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Someone else connected while this call waited for the lock. Kept only if it is still the right
                // target: the pin may have changed since.
                var current = Current;
                if (current != null)
                {
                    if (PinnedEngine == null || current.Engine.Is(PinnedEngine)) return true;
                    Teardown(current);
                }

                var candidates = DiscoverEngines();
                if (candidates.Count == 0)
                {
                    StdioTransport.LogInfo("No engine plugin discovered.");
                    return false;
                }

                // ⚠️ The pin is honoured on EVERY reconnect, not just the first one. A Unity domain reload
                // drops the pipe and this method runs again — which is exactly when the target used to
                // change under the caller's feet.
                if (PinnedEngine != null)
                {
                    var matching = candidates.FindAll(c => c.Is(PinnedEngine));
                    if (matching.Count == 0)
                    {
                        StdioTransport.LogInfo(
                            $"Pinned to '{PinnedEngine}' but only [{DescribeAll(candidates)}] are running: " +
                            "staying disconnected rather than talking to the wrong one.");
                        return false;
                    }
                    if (matching.Count > 1)
                        StdioTransport.LogInfo(
                            $"Pinned to '{PinnedEngine}', which matches [{DescribeAll(matching)}]: taking the first. " +
                            "Pin by pid or project to choose.");
                    candidates = matching;
                }
                else if (candidates.Count > 1)
                {
                    StdioTransport.LogInfo(
                        $"{candidates.Count} engines available [{DescribeAll(candidates)}] and none pinned: " +
                        "taking the first. Pin one with AKER_MCP_ENGINE or the engine_status tool.");
                }

                foreach (var candidate in candidates)
                {
                    StdioTransport.LogInfo($"Attempting connection to pipe: {candidate.Pipe}");

                    var connection = await Open(candidate, 5000, ct).ConfigureAwait(false);
                    if (connection == null) continue;

                    Volatile.Write(ref _connection, connection);

                    // Each task is bound to ITS connection: whatever it does when it ends, it does to that one.
                    var lifetime = connection.Lifetime.Token;
                    _ = Task.Run(() => ListenForResponses(connection, lifetime));
                    _ = Task.Run(() => RunHeartbeat(connection, lifetime));

                    StdioTransport.LogInfo(
                        $"Connected to {candidate} via {candidate.Pipe} (Client Protocol: v{candidate.ProtocolVersion})");

                    var skew = DescribeVersionSkew(candidate.ProtocolVersion, IpcConstants.ProtocolVersion);
                    if (skew.breaking) StdioTransport.LogError(skew.message!);
                    else if (skew.message != null) StdioTransport.LogInfo(skew.message);

                    OnConnected?.Invoke();
                    return true;
                }

                StdioTransport.LogInfo("No engine plugin available after discovery scan.");
                return false;
            }
            finally
            {
                _connectLock.Release();
            }
        }

        /// <summary>Raised once a plugin is connected, so per-session state can be cleared.</summary>
        public event Action? OnConnected;

        /// <summary>
        /// What to say about a plugin speaking a different protocol version than the server.
        ///
        /// Only the major matters. A minor difference means one side knows a method the other
        /// does not, which is precisely what every optional capability here already is: the
        /// older peer answers NOT_SUPPORTED and the caller carries on. Reporting that as
        /// "PROTOCOL MISMATCH! Expect connection errors" — which is what this did before —
        /// taught everyone to ignore the one line that would have mattered when a major really
        /// did change.
        /// </summary>
        public static (bool breaking, string? message) DescribeVersionSkew(string clientVersion, string serverVersion)
        {
            if (string.Equals(clientVersion, serverVersion, StringComparison.Ordinal))
                return (false, null);

            static int Major(string v)
            {
                var dot = v.IndexOf('.');
                var head = dot > 0 ? v.Substring(0, dot) : v;
                return int.TryParse(head, out var n) ? n : -1;
            }

            int clientMajor = Major(clientVersion), serverMajor = Major(serverVersion);
            if (clientMajor < 0 || serverMajor < 0)
                return (true, $"PROTOCOL MISMATCH! Server is v{serverVersion} but the plugin reports " +
                              $"'{clientVersion}', which is not a version number. Expect connection errors.");

            if (clientMajor != serverMajor)
                return (true, $"PROTOCOL MISMATCH! Server is v{serverVersion} but the plugin is " +
                              $"v{clientVersion}: different major versions do not interoperate. " +
                              "Update the plugin's bundled AkerMcp.Client/AkerMcp.Shared.");

            // By number, not by text: compared as strings "1.10.0" sorted before "1.7.0".
            var older = Version.TryParse(clientVersion, out var cv) && Version.TryParse(serverVersion, out var sv)
                ? (cv < sv ? "plugin" : "server")
                : (string.CompareOrdinal(clientVersion, serverVersion) < 0 ? "plugin" : "server");
            return (false, $"Protocol v{clientVersion} (plugin) vs v{serverVersion} (server): compatible. " +
                           $"Anything added after the {older}'s version answers NOT_SUPPORTED.");
        }

        private static string DescribeAll(List<EngineIdentity> engines)
            => string.Join(", ", engines.ConvertAll(e => e.ToString()));

        /// <summary>"30s", "1.5s", "0.25s": a sub-second deadline used to print as "0s".</summary>
        private static string Seconds(int ms)
            => (ms / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + "s";

        /// <summary>
        /// The one round trip every forwarding method makes: wait out a reconnect if asked, send, wait for the answer
        /// within the deadline. There used to be five copies of it, and they had drifted — only some knew about a
        /// disposed channel, none bounded the write.
        /// </summary>
        private async Task<(IpcResponse? response, string? error)> RoundTrip(
            IpcRequest request, string label, int deadlineMs, bool waitForReconnect, CancellationToken ct)
        {
            var connection = Current;
            if (connection == null)
            {
                if (!waitForReconnect)
                    return (null, $"{EngineDisconnectedPrefix} No engine connected.");
                // Most common cause: Unity is mid domain-reload. Give the retry loop a chance instead of failing the
                // call (and burning a model round-trip on a transient state).
                if (!await WaitForConnection(ReconnectGraceMs, ct).ConfigureAwait(false))
                    return (null,
                        $"{EngineDisconnectedPrefix} No engine connected (waited {Seconds(ReconnectGraceMs)}). " +
                        "If Unity is compiling, retry shortly; otherwise check that the AkerMcp plugin is running in the editor.");
                connection = Current;
                if (connection == null)
                    return (null, $"{EngineDisconnectedPrefix} Engine connection dropped again — retry shortly.");
            }

            request.Id = Interlocked.Increment(ref _nextRequestId);
            var tcs = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Pending[request.Id] = tcs;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(deadlineMs);
            try
            {
                try
                {
                    // Within the deadline too: a write to a pipe nobody reads (an engine stopped in a debugger) used
                    // to wait forever, and with it the whole server.
                    await connection.Channel.SendRequest(request, deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // A frame may be half written: the pipe cannot carry another one.
                    Teardown(connection);
                    return (null,
                        $"{EngineDisconnectedPrefix} The engine did not take '{label}' within {Seconds(deadlineMs)}: " +
                        "it is not reading its pipe (a debugger break, a frozen editor). The connection was dropped " +
                        "and is made again automatically.");
                }

                using var reg = deadline.Token.Register(() => tcs.TrySetCanceled());
                return (await tcs.Task.ConfigureAwait(false), null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // caller cancelled — propagate
            }
            catch (OperationCanceledException)
            {
                // Either the per-call timeout fired or the connection closed and cancelled the pending request.
                // Disambiguate so the model gets an actionable error.
                if (connection.IsClosed)
                    return (null,
                        $"{EngineDisconnectedPrefix} Engine disconnected while '{label}' was in flight " +
                        "(usually a Unity domain reload after script recompilation). It reconnects automatically — retry shortly.");
                return (null, $"Tool '{label}' timed out after {Seconds(deadlineMs)} (engine still connected).");
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                // ObjectDisposedException: the send overlapped the connection closing. It used to surface as "Cannot
                // access a disposed object" without the prefix, and the reload handling in refresh_scripts and
                // enter_play did not recognise it.
                return (null, $"{EngineDisconnectedPrefix} Pipe error during '{label}': {ex.Message}. Retry shortly.");
            }
            finally
            {
                connection.Pending.TryRemove(request.Id, out _);
            }
        }

        private static byte[]? Utf8(JsonElement? arguments)
            => arguments is { ValueKind: not JsonValueKind.Undefined } a
                ? System.Text.Encoding.UTF8.GetBytes(a.GetRawText())
                : null;

        private static ToolResult AsToolResult((IpcResponse? response, string? error) answer)
        {
            if (answer.error != null) return ToolResult.Error(answer.error);
            var response = answer.response!;
            if (!response.Success)
                return ToolResult.Error(response.Error ?? "Unknown engine error");
            return ToolResult.Text(response.Payload != null
                ? System.Text.Encoding.UTF8.GetString(response.Payload)
                : "OK");
        }

        public async Task<ToolResult> ForwardToolCall(string method, JsonElement? arguments, CancellationToken ct,
            int? timeoutMs = null)
        {
            var request = new IpcRequest { Method = method, Payload = Utf8(arguments) };
            return AsToolResult(await RoundTrip(request, method, timeoutMs ?? DefaultRequestTimeoutMs,
                waitForReconnect: true, ct).ConfigureAwait(false));
        }

        /// <summary>
        /// Sends a request carrying BOTH JSON metadata (Payload) and a raw binary blob
        /// (Binary) into the engine — the inbound counterpart of ForwardBinaryToolCall.
        /// Used by create_sprite to ship a server-rasterized PNG to the engine plugin.
        /// </summary>
        public async Task<ToolResult> ForwardSpriteImport(
            string metadataJson, byte[] binary, CancellationToken ct,
            int? timeoutMs = null)
        {
            var request = new IpcRequest
            {
                Method = IpcConstants.Methods.ImportSprite,
                Payload = System.Text.Encoding.UTF8.GetBytes(metadataJson),
                Binary = binary
            };
            return AsToolResult(await RoundTrip(request, IpcConstants.Methods.ImportSprite,
                timeoutMs ?? DefaultRequestTimeoutMs, waitForReconnect: true, ct).ConfigureAwait(false));
        }

        /// <summary>
        /// Ships a server-synthesized WAV (Binary) + JSON metadata (Payload) into the engine,
        /// the audio counterpart of ForwardSpriteImport. Used by create_sound.
        /// </summary>
        public async Task<ToolResult> ForwardSoundImport(
            string metadataJson, byte[] binary, CancellationToken ct,
            int? timeoutMs = null)
        {
            var request = new IpcRequest
            {
                Method = IpcConstants.Methods.ImportSound,
                Payload = System.Text.Encoding.UTF8.GetBytes(metadataJson),
                Binary = binary
            };
            return AsToolResult(await RoundTrip(request, IpcConstants.Methods.ImportSound,
                timeoutMs ?? DefaultRequestTimeoutMs, waitForReconnect: true, ct).ConfigureAwait(false));
        }

        public async Task<BinaryToolCallResult> ForwardBinaryToolCall(
            string method, JsonElement? arguments, CancellationToken ct)
        {
            var request = new IpcRequest { Method = method, Payload = Utf8(arguments) };
            var (response, error) = await RoundTrip(request, method, DefaultRequestTimeoutMs,
                waitForReconnect: true, ct).ConfigureAwait(false);

            if (error != null) return new BinaryToolCallResult { Error = error };
            if (!response!.Success)
                return new BinaryToolCallResult { ErrorCode = response.ErrorCode, Error = response.Error };
            return new BinaryToolCallResult { Bytes = response.Payload, ContentType = response.ContentType };
        }

        /// <summary>
        /// A read-only engine query whose answer is text. A failure comes back as text starting with "Error:" — what
        /// the callers test for — including a timeout, which used to escape as a TaskCanceledException.
        /// </summary>
        /// <param name="waitForReconnect">
        /// Wait out a reconnect, as tool calls do. Off by default: the resource reads and the window lookups behind an
        /// OS-level fallback would rather answer at once.
        /// </param>
        public async Task<string> ForwardResourceRead(string method, CancellationToken ct, bool waitForReconnect = false)
        {
            var (response, error) = await RoundTrip(new IpcRequest { Method = method }, method,
                ResourceReadTimeoutMs, waitForReconnect, ct).ConfigureAwait(false);

            if (error != null) return $"Error: {error}";
            if (!response!.Success) return $"Error: {response.Error}";
            return response.Payload != null
                ? System.Text.Encoding.UTF8.GetString(response.Payload)
                : "";
        }

        private async Task ListenForResponses(Connection connection, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var response = await connection.Channel.ReceiveResponse(ct).ConfigureAwait(false);
                    if (connection.Pending.TryGetValue(response.Id, out var tcs))
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
                // The channel was closed on another thread.
            }
            catch (Exception ex)
            {
                StdioTransport.LogInfo($"Engine connection lost: {ex.Message}");
            }
            finally
            {
                Teardown(connection);
            }
        }

        private async Task RunHeartbeat(Connection connection, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(HeartbeatIntervalMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                var pingId = Interlocked.Increment(ref _nextRequestId);
                var tcs = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.Pending[pingId] = tcs;

                try
                {
                    // One deadline for the write and the answer: a ping that cannot even be written is as much a
                    // zombie as one that is never answered.
                    using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    pingCts.CancelAfter(HeartbeatTimeoutMs);
                    using var reg = pingCts.Token.Register(() => tcs.TrySetCanceled());

                    await connection.Channel.SendRequest(new IpcRequest
                    {
                        Id = pingId,
                        Method = IpcConstants.Methods.Ping
                    }, pingCts.Token).ConfigureAwait(false);

                    await tcs.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    StdioTransport.LogInfo($"Heartbeat failed — dropping zombie connection: {ex.GetType().Name}: {ex.Message}");
                    Teardown(connection);
                    return;
                }
                finally
                {
                    connection.Pending.TryRemove(pingId, out _);
                }
            }
        }

        /// <summary>
        /// Closes this connection, and clears it as the current one only if it still is: a task of an old connection
        /// can no longer drop the one that replaced it.
        /// </summary>
        private void Teardown(Connection connection)
        {
            Interlocked.CompareExchange(ref _connection, null, connection);
            connection.Close();
        }

        /// <summary>
        /// Drops the current connection so the retry loop picks a target again. Public because
        /// <c>engine_status</c> uses it to switch engines: pinning alone would not move an already
        /// established connection.
        /// </summary>
        public void Disconnect()
        {
            if (Current is { } connection) Teardown(connection);
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
