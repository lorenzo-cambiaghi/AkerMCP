using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using AkerMcp.Shared.Abstraction;
using AkerMcp.Shared.Ipc;

namespace AkerMcp.Client
{
    public abstract class EnginePluginBase : IDisposable
    {
        private NamedPipeServerStream? _pipeServer;
        private IpcChannel? _channel;
        private IpcRequestHandler? _handler;
        private PluginDiscovery? _discovery;
        private CancellationTokenSource? _cts;
        private Task? _pipeServerTask;
        private bool _disposed;
        private bool _running;

        protected ClientConfiguration Config { get; } = new ClientConfiguration();

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
            // Close the pipe server first so any blocking WaitForConnectionAsync unblocks.
            try { _pipeServer?.Close(); } catch { }

            // Wait for RunPipeServer to fully exit (incl. its finally) BEFORE a
            // possible subsequent Start(). Otherwise the old task's finally could
            // dispose the new pipe instance created by the new Start() call.
            try { _pipeServerTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _pipeServerTask = null;

            try { _channel?.Dispose(); } catch { }
            try { _pipeServer?.Dispose(); } catch { }
            // Lock file removal must always run, even if pipe disposal threw above.
            try { _discovery?.Dispose(); } catch { }

            Log("AkerMcp plugin stopped.");
        }

        private async Task RunPipeServer(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? pipeServer = null;
                IpcChannel? channel = null;
                try
                {
                    pipeServer = new NamedPipeServerStream(
                        Config.PipeName!,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    // Expose to Stop() so it can Close() to unblock WaitForConnectionAsync.
                    _pipeServer = pipeServer;

                    Log("Waiting for MCP server connection...");
                    await pipeServer.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    Log("MCP server connected.");

                    channel = new IpcChannel(pipeServer);
                    _channel = channel;
                    await HandleConnection(channel, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // Pipe was disposed by Stop() on another thread.
                    if (!_running || ct.IsCancellationRequested) break;
                }
                catch (Exception ex)
                {
                    if (!_running || ct.IsCancellationRequested) break;
                    LogError($"Pipe server error: {ex.Message}");
                    try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
                finally
                {
                    // Dispose only THIS iteration's instances (locals). NEVER touch
                    // the fields directly: if Stop() timed out and a subsequent
                    // Start() already replaced them, we'd dispose the new instance.
                    try { channel?.Dispose(); } catch { }
                    try { pipeServer?.Dispose(); } catch { }
                    // Clear the fields only if they still point to ours.
                    Interlocked.CompareExchange(ref _channel, null, channel);
                    Interlocked.CompareExchange(ref _pipeServer, null, pipeServer);
                }
            }
        }

        private async Task HandleConnection(IpcChannel channel, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var request = await channel.ReceiveRequest(ct).ConfigureAwait(false);
                var response = await _handler!.HandleRequest(request, ct).ConfigureAwait(false);
                await channel.SendResponse(response, ct).ConfigureAwait(false);
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
