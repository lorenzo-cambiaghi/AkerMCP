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

            await engine.TryDiscoverAndConnect(cts.Token);

            var toolRegistry = new ToolRegistry(engine);
            var resourceRegistry = new ResourceRegistry(engine);
            var server = new McpServer(transport, toolRegistry, resourceRegistry);

            await server.Run(cts.Token);
        }
    }
}
