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

        // Tracked so Stop() can force-close them: under Unity's Mono the
        // CancellationToken on WaitForConnectionAsync/ReadAsync is not reliably
        // honored — disposing the pipe is the only dependable way to unblock.
        private NamedPipeServerStream? _listeningPipe;
        private readonly List<NamedPipeServerStream> _activePipes = new List<NamedPipeServerStream>();

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
        protected virtual IBuildManager? CreateBuildManager() => null;

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
                CreateScreenCapture(),
                CreateBuildManager());

            _cts = new CancellationTokenSource();

            _pipeServerTask = Task.Run(() => RunPipeServer(_cts.Token));
            Log($"AkerMcp Client v{AkerMcp.Shared.Ipc.IpcConstants.ProtocolVersion} started. Pipe: {_discovery.PipeName}");
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;

            try { _cts?.Cancel(); } catch { }

            // Forza la chiusura delle pipe per sbloccare WaitForConnectionAsync e
            // le ReceiveRequest in corso (la cancellazione via token non basta su Mono).
            try { Interlocked.Exchange(ref _listeningPipe, null)?.Dispose(); } catch { }
            NamedPipeServerStream[] pipes;
            lock (_connectionsLock)
            {
                pipes = _activePipes.ToArray();
                _activePipes.Clear();
            }
            foreach (var pipe in pipes)
            {
                try { pipe.Dispose(); } catch { }
            }

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
                    _listeningPipe = pipeServer;

                    Log("Waiting for MCP server connection...");
                    await pipeServer.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    Log("MCP server connected.");

                    // Passa proprietà al task di gestione e rimetti subito in ascolto
                    var connectedPipe = pipeServer;
                    pipeServer = null; // impedisce il dispose nel finally
                    _listeningPipe = null;

                    var connectionTask = Task.Run(() =>
                        HandleSingleConnection(connectedPipe, ct));

                    lock (_connectionsLock)
                    {
                        // Rimuovi task completati
                        _activeConnections.RemoveAll(t => t.IsCompleted);
                        _activeConnections.Add(connectionTask);
                        _activePipes.Add(connectedPipe);
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

                    // Gestisci in background così il read loop continua a consumare
                    // le richieste successive — soprattutto i ping dell'heartbeat del
                    // server, che altrimenti resterebbero non letti dietro una chiamata
                    // lunga (>5-15s) facendo scattare il rilevamento "zombie" e la
                    // disconnessione a metà chiamata. È sicuro: IpcChannel serializza
                    // le scritture col suo write lock e il server correla le risposte
                    // per Id, quindi l'ordine di risposta non conta.
                    var boundChannel = channel;
                    _ = Task.Run(() => HandleRequestAndRespond(boundChannel, request, ct));
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
                lock (_connectionsLock) { _activePipes.Remove(pipe); }
                try { channel?.Dispose(); } catch { }
                try { pipe.Dispose(); } catch { }
                Log($"Client disconnected (remaining: {Math.Max(0, ActiveConnectionCount - 1)})");
            }
        }

        private async Task HandleRequestAndRespond(IpcChannel channel, IpcRequest request, CancellationToken ct)
        {
            try
            {
                var response = await _handler!.HandleRequest(request, ct).ConfigureAwait(false);
                await channel.SendResponse(response, ct).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Connessione chiusa mentre la richiesta era in lavorazione:
                // non c'è più nessuno a cui rispondere.
            }
            catch (IOException)
            {
                // Pipe rotta a metà risposta — il client è già andato via.
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (_running && !ct.IsCancellationRequested)
                    LogError($"Failed to respond to '{request.Method}': {ex.Message}");
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
