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
        // Read by the pipe threads, written by Start/Stop on the editor's thread.
        private volatile bool _running;
        private readonly List<Task> _activeConnections = new List<Task>();
        private readonly object _connectionsLock = new object();

        // Tracked so Stop() can force-close them: under Unity's Mono the
        // CancellationToken on WaitForConnectionAsync/ReadAsync is not reliably
        // honored — disposing the pipe is the only dependable way to unblock.
        private NamedPipeServerStream? _listeningPipe;
        private readonly List<NamedPipeServerStream> _activePipes = new List<NamedPipeServerStream>();

        protected ClientConfiguration Config { get; } = new ClientConfiguration();

        /// <summary>Number of MCP servers currently connected to this editor plugin.</summary>
        public int ActiveConnectionCount
        {
            get { lock (_connectionsLock) return _activeConnections.Count(t => !t.IsCompleted); }
        }

        /// <summary>Whether dynamic C# execution (`execute`) is permitted on this editor.</summary>
        public bool EnableExecuteTool
        {
            get => Config.EnableExecuteTool;
            set => Config.EnableExecuteTool = value;
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
        protected virtual ISpriteImporter? CreateSpriteImporter() => null;
        protected virtual ISceneManager? CreateSceneManager() => null;
        protected virtual IPlayModeController? CreatePlayModeController() => null;
        protected virtual IInputSimulator? CreateInputSimulator() => null;
        protected virtual ISoundImporter? CreateSoundImporter() => null;

        /// <summary>
        /// Override in an editor where the user can grant and revoke the agent's access. The
        /// server then declines in the engine's name instead of routing around a refusal it
        /// cannot tell apart from a missing capability — see <see cref="IAgentConsent"/>.
        /// </summary>
        protected virtual IAgentConsent? CreateAgentConsent() => null;

        protected abstract void Log(string message);
        protected abstract void LogError(string message);

        public void Start()
        {
            if (_running) return;

            // Everything is built before anything is announced, and _running goes true only once it all exists. It
            // used to go true first: an adapter whose Create* threw left a plugin that said it was running, could not
            // be started again, and was unreachable until the next domain reload.
            var capabilities = CreateCapabilities();
            var editorContext = CreateEditorContext();

            _handler = new IpcRequestHandler(
                CreateSceneGraph(),
                capabilities,
                CreateDispatcher(),
                Config,
                CreateAssetManager(),
                editorContext,
                CreateCompilationSupport(),
                CreateCodeExecutor(),
                CreateScreenCapture(),
                CreateBuildManager(),
                CreateSpriteImporter(),
                CreateSceneManager(),
                CreatePlayModeController(),
                CreateInputSimulator(),
                CreateSoundImporter(),
                CreateAgentConsent());

            _discovery = new PluginDiscovery(capabilities.EngineName, capabilities.EngineVersion,
                ProjectPathOf(editorContext));
            Config.PipeName = _discovery.PipeName;

            _cts = new CancellationTokenSource();
            _running = true;

            _pipeServerTask = Task.Run(() => RunPipeServer(_cts.Token));
            Log($"AkerMcp Client v{AkerMcp.Shared.Ipc.IpcConstants.ProtocolVersion} started. Pipe: {_discovery.PipeName}");
        }

        /// <summary>
        /// The open project, for the lock file. Start runs on the editor's main thread in every engine, which is where
        /// the editors answer this; an editor that cannot answer yet is announced without a project.
        /// </summary>
        private static string? ProjectPathOf(IEditorContext? editorContext)
        {
            try { return editorContext?.GetProjectPath(); }
            catch { return null; }
        }

        public void Stop()
        {
            if (!_running) return;

            NamedPipeServerStream? listening;
            NamedPipeServerStream[] pipes;
            lock (_connectionsLock)
            {
                // Under the same lock the pipe thread takes to publish a pipe: either it published the pipe before
                // this point and the pipe is closed here, or it sees _running false and closes the pipe itself. Before,
                // a pipe created in between was missed by both, and on Mono it leaked until the domain unloaded.
                _running = false;
                listening = _listeningPipe;
                _listeningPipe = null;
                pipes = _activePipes.ToArray();
                _activePipes.Clear();
            }

            try { _cts?.Cancel(); } catch { }

            // Forza la chiusura delle pipe per sbloccare WaitForConnectionAsync e
            // le ReceiveRequest in corso (la cancellazione via token non basta su Mono).
            try { listening?.Dispose(); } catch { }
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
                    lock (_connectionsLock)
                    {
                        if (!_running) break; // Stop() ran meanwhile: the finally disposes the pipe
                        _listeningPipe = pipeServer;
                    }

                    Log("Waiting for MCP server connection...");
                    await pipeServer.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    Log("MCP server connected.");

                    // Passa proprietà al task di gestione e rimetti subito in ascolto
                    var connectedPipe = pipeServer;
                    lock (_connectionsLock)
                    {
                        _listeningPipe = null;
                        if (!_running) break; // the finally disposes it: Stop() has already swept the list
                        pipeServer = null; // impedisce il dispose nel finally
                        _activePipes.Add(connectedPipe);
                        // Rimuovi task completati
                        _activeConnections.RemoveAll(t => t.IsCompleted);
                        _activeConnections.Add(Task.Run(() => HandleSingleConnection(connectedPipe, ct)));
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

        private async Task HandleSingleConnection(NamedPipeServerStream pipe, CancellationToken pluginCt)
        {
            IpcChannel? channel = null;
            // Ends with this connection: the requests still running for a server that has gone (a refresh_scripts
            // polling for 150 s) stop when it leaves, instead of when the plugin does.
            var connection = CancellationTokenSource.CreateLinkedTokenSource(pluginCt);
            var ct = connection.Token;
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
                // Cancelled, not disposed: the requests still holding the token may read it after this point.
                try { connection.Cancel(); } catch { }
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
                try
                {
                    await channel.SendResponse(response, ct).ConfigureAwait(false);
                }
                catch (IpcFrameTooLargeException tooLarge)
                {
                    // Answered with an error the caller can act on, instead of no answer at all: the server would
                    // have waited out its deadline and reported a timeout.
                    await channel.SendResponse(IpcResponse.Fail(request.Id,
                        $"The result of '{request.Method}' is too large to send: {tooLarge.Message} " +
                        "Ask for less (a narrower query, a smaller capture, a shorter return value)."), ct).ConfigureAwait(false);
                }
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
