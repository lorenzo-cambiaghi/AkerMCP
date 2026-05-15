using System;
using System.Threading;
using System.Threading.Tasks;

namespace AkerMcp.Server
{
    class Program
    {
        static async Task Main(string[] args)
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            if (OperatingSystem.IsWindows())
                ScreenCaptureService.EnsureDpiAwareness();

            using var transport = new StdioTransport();
            using var engine = new EngineConnection();

            StdioTransport.LogInfo($"AkerMcp Server v{AkerMcp.Shared.Ipc.IpcConstants.ProtocolVersion} starting...");

            await engine.TryDiscoverAndConnect(cts.Token);
            var retryTask = Task.Run(() => RetryEngineConnection(engine, cts.Token));

            var toolRegistry = new ToolRegistry(engine);
            var resourceRegistry = new ResourceRegistry(engine);
            var server = new McpServer(transport, toolRegistry, resourceRegistry);

            try
            {
                await server.Run(cts.Token);
            }
            finally
            {
                // Stop the background retry loop and wait briefly so it doesn't
                // touch a disposed CancellationTokenSource after Main returns.
                cts.Cancel();
                try { await retryTask.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch { /* timeout or already-faulted task — process is exiting anyway */ }
            }
        }

        private static async Task RetryEngineConnection(EngineConnection engine, CancellationToken ct)
        {
            var delayMs = 2000;
            const int maxDelayMs = 10000;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (engine.IsConnected)
                    {
                        delayMs = 2000;
                        await Task.Delay(2000, ct).ConfigureAwait(false);
                        continue;
                    }

                    await Task.Delay(delayMs, ct).ConfigureAwait(false);

                    try
                    {
                        if (await engine.TryDiscoverAndConnect(ct).ConfigureAwait(false))
                        {
                            StdioTransport.LogInfo("Engine connected via background retry loop.");
                            delayMs = 2000;
                            continue;
                        }
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        StdioTransport.LogInfo($"Retry loop error: {ex.Message}");
                    }

                    delayMs = Math.Min(maxDelayMs, delayMs * 2);
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (ObjectDisposedException) { /* cts already disposed during shutdown */ }
        }
    }
}

