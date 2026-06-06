using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AkerMcp.Shared.Abstraction;
using AkerMcp.Shared.Ipc;

namespace AkerMcp.Client
{
    public abstract class EnginePluginBase : IDisposable
    {
        private IpcRequestHandler? _handler;
        private PluginDiscovery? _discovery;
        private CancellationTokenSource? _cts;
        private Task? _pipeServerTask;
        private bool _disposed;
        private bool _running;
        private readonly List<Task> _activeConnections = new List<Task>();
        private readonly object _connectionsLock = new object();

        protected ClientConfiguration Config { get; } = new ClientConfiguration();

        private int ActiveConnectionCount
        {
            get { lock (_connectionsLock) return _activeConnections.Count(t => !t.IsCompleted); }
        }

        protected abstract ISceneGraph CreateSceneGraph();
        protected abstract IEngineCapabilities CreateCapabilities();
        protected abstract IMainThreadDispatcher CreateDispatcher();
        protected virtual IAssetManager? CreateAssetManager() => null;
        protected virtual IEditorContext? CreateEditorContext() => null;
        protected virtual ICompilationSupport? CreateCompilationSupport() => null;
        protected virtual ICodeExecutor? CreateCodeExecutor() => null;
        protected virtual IScreenCapture? CreateScreenCapture() => null;

        protected abstract void Log(string message);
        protected abstract void LogError(string message);

        public void Start()
        {
            if (_running) return;
            _running = true;

            var capabilities = CreateCapabilities();
            _discovery = new PluginDiscovery(capabilities.EngineName, capabilities.EngineVersion);
            Config.PipeName = _discovery.PipeName;

            _handler = new IpcRequestHandler(
                CreateSceneGraph(),
                capabilities,
                CreateDispatcher(),
                Config,
                CreateAssetManager(),
                CreateEditorContext(),
                CreateCompilationSupport(),
                CreateCodeExecutor(),
                CreateScreenCapture());

            _cts = new CancellationTokenSource();

            _pipeServerTask = Task.Run(() => RunPipeServer(_cts.Token));
            Log($"AkerMcp Client v{AkerMcp.Shared.Ipc.IpcConstants.ProtocolVersion} started. Pipe: {_discovery.PipeName}");
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;

            try { _cts?.Cancel(); } catch { }

            // Aspetta che tutte le connessioni attive si chiudano
            Task[] connections;
            lock (_connectionsLock)
            {
                connections = _activeConnections.ToArray();
                _activeConnections.Clear();
            }
            try { Task.WaitAll(connections, TimeSpan.FromSeconds(2)); } catch { }

            try { _pipeServerTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _pipeServerTask = null;

            try { _discovery?.Dispose(); } catch { }

            Log("AkerMcp plugin stopped.");
        }

        private async Task RunPipeServer(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? pipeServer = null;
                try
                {
                    pipeServer = new NamedPipeServerStream(
                        Config.PipeName!,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    Log("Waiting for MCP server connection...");
                    await pipeServer.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    Log("MCP server connected.");

                    // Passa proprietà al task di gestione e rimetti subito in ascolto
                    var connectedPipe = pipeServer;
                    pipeServer = null; // impedisce il dispose nel finally

                    var connectionTask = Task.Run(() =>
                        HandleSingleConnection(connectedPipe, ct));

                    lock (_connectionsLock)
                    {
                        // Rimuovi task completati
                        _activeConnections.RemoveAll(t => t.IsCompleted);
                        _activeConnections.Add(connectionTask);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException)
                {
                    if (!_running || ct.IsCancellationRequested) break;
                }
                catch (Exception ex)
                {
                    if (!_running || ct.IsCancellationRequested) break;
                    LogError($"Pipe server error: {ex.Message}");
                    try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch { break; }
                }
                finally
                {
                    // Se pipeServer non è null, vuol dire che non siamo arrivati
                    // al punto di passarlo al task -> dispose sicuro
                    try { pipeServer?.Dispose(); } catch { }
                }
            }
        }

        private async Task HandleSingleConnection(NamedPipeServerStream pipe, CancellationToken ct)
        {
            IpcChannel? channel = null;
            try
            {
                channel = new IpcChannel(pipe);
                Log($"Client connected (total: {ActiveConnectionCount})");

                while (!ct.IsCancellationRequested)
                {
                    var request = await channel.ReceiveRequest(ct).ConfigureAwait(false);
                    var response = await _handler!.HandleRequest(request, ct).ConfigureAwait(false);
                    await channel.SendResponse(response, ct).ConfigureAwait(false);
                }
            }
            catch (EndOfStreamException)
            {
                Log("MCP server client disconnected.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (_running)
                    LogError($"Client connection error: {ex.Message}");
            }
            finally
            {
                try { channel?.Dispose(); } catch { }
                try { pipe.Dispose(); } catch { }
                Log($"Client disconnected (remaining: {Math.Max(0, ActiveConnectionCount - 1)})");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
