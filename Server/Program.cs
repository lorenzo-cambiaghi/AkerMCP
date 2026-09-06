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

            // Init the per-OS capture impl (DPI awareness on Win, no-op on Mac).
            // Touching the property triggers initialization.
            _ = AkerMcp.Server.Platform.PlatformScreenCapture.Current;

            using var transport = new StdioTransport();
            using var engine = new EngineConnection();

            StdioTransport.LogInfo($"AkerMcp Server v{AkerMcp.Shared.Ipc.IpcConstants.ProtocolVersion} starting...");

            await engine.TryDiscoverAndConnect(cts.Token);
            var retryTask = Task.Run(() => RetryEngineConnection(engine, cts.Token));

            var toolRegistry = new ToolRegistry(engine);
            try
            {
                var (kept, dropped) = toolRegistry.ApplyProfile(
                    ToolProfiles.Resolve(ProfileArgument(args)),
                    ToolProfiles.NamesFromEnvironment("AKER_MCP_TOOLS_INCLUDE"),
                    ToolProfiles.NamesFromEnvironment("AKER_MCP_TOOLS_EXCLUDE"));
                StdioTransport.LogInfo(
                    $"Tool profile '{toolRegistry.Profile}': {kept.Count} tools" +
                    (dropped.Count > 0 ? $", not loaded: {string.Join(", ", dropped)}" : ""));
            }
            catch (ArgumentException ex)
            {
                StdioTransport.LogError(ex.Message);
                Environment.Exit(2);
            }
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

        /// <summary>`--profile core` or `--profile=core` on the command line, else null.</summary>
        private static string? ProfileArgument(string[] args)
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--profile" && i + 1 < args.Length) return args[i + 1];
                if (args[i].StartsWith("--profile=", StringComparison.Ordinal)) return args[i].Substring("--profile=".Length);
            }
            return null;
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

