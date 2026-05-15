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

            _ = Task.Run(() => RunPipeServer(_cts.Token));
            Log($"AkerMcp Client v{AkerMcp.Shared.Ipc.IpcConstants.ProtocolVersion} started. Pipe: {_discovery.PipeName}");
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;

            _cts?.Cancel();
            
            // Close the pipe server first to unblock any waiting threads immediately
            try { _pipeServer?.Close(); } catch { }

            _channel?.Dispose();
            _pipeServer?.Dispose();
            _discovery?.Dispose();

            Log("AkerMcp plugin stopped.");
        }

        private async Task RunPipeServer(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _pipeServer = new NamedPipeServerStream(
                        Config.PipeName!,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    Log("Waiting for MCP server connection...");
                    await _pipeServer.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    Log("MCP server connected.");

                    _channel = new IpcChannel(_pipeServer);
                    await HandleConnection(_channel, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!_running || ct.IsCancellationRequested) break;
                    LogError($"Pipe server error: {ex.Message}");
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                finally
                {
                    _channel?.Dispose();
                    _channel = null;
                    _pipeServer?.Dispose();
                    _pipeServer = null;
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
