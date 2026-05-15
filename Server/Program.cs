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
            _ = Task.Run(async () => await RetryEngineConnection(engine, cts.Token));

            var toolRegistry = new ToolRegistry(engine);
            var resourceRegistry = new ResourceRegistry(engine);
            var server = new McpServer(transport, toolRegistry, resourceRegistry);

            await server.Run(cts.Token);
        }

        private static async Task RetryEngineConnection(EngineConnection engine, CancellationToken ct)
        {
            var delayMs = 2000;
            const int maxDelayMs = 10000;

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
    }
}

