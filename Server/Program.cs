using System;
using System.Threading;
using System.Threading.Tasks;
using AkerMcp.Server.Setup;

namespace AkerMcp.Server
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            // Human-facing CLI helpers (`setup`, `doctor`, `--version`, `--help`) exit
            // cleanly without touching stdio JSON-RPC.
            if (SetupCli.TryRun(args, out var cliExitCode))
                return cliExitCode;

            // Resolve the command line and the tool profile before anything is opened: a bad
            // name or an unknown flag is a configuration error, answered with exit code 2 and
            // nothing to clean up. An unknown flag used to be ignored, and a `--profile` with no
            // value silently meant the default profile.
            string profile;
            try
            {
                profile = ToolProfiles.Resolve(ProfileArgument(args));
            }
            catch (ArgumentException ex)
            {
                StdioTransport.LogError(ex.Message);
                return 2;
            }

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

            StdioTransport.LogInfo(
                $"AkerMcp Server v{ServerVersion.Product} starting " +
                $"(IPC protocol {AkerMcp.Shared.Ipc.IpcConstants.ProtocolVersion})...");

            var toolRegistry = new ToolRegistry(engine);
            var (kept, dropped) = toolRegistry.ApplyProfile(
                profile,
                ToolProfiles.NamesFromEnvironment("AKER_MCP_TOOLS_INCLUDE"),
                ToolProfiles.NamesFromEnvironment("AKER_MCP_TOOLS_EXCLUDE"));
            StdioTransport.LogInfo(
                $"Tool profile '{toolRegistry.Profile}': {kept.Count} tools" +
                (dropped.Count > 0 ? $", not loaded: {string.Join(", ", dropped)}" : ""));
            var resourceRegistry = new ResourceRegistry(engine);
            var server = new McpServer(transport, toolRegistry, resourceRegistry);

            // In the background, after the registry has subscribed to OnConnected: the first
            // discovery used to run before the MCP loop started, so `initialize` waited up to five
            // seconds for each plugin that had announced itself and did not answer. A tool call
            // that arrives first waits for the connection anyway (EngineConnection's grace period).
            var retryTask = Task.Run(() => ConnectAndKeepConnected(engine, cts.Token));

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
                catch { /* timeout or already-faulted task: the process is exiting anyway */ }
            }

            return 0;
        }

        /// <summary>
        /// `--profile core` or `--profile=core` on the command line, else null. Anything else on the
        /// command line is refused: this server has no other option, and a typo deserves an error.
        /// </summary>
        private static string? ProfileArgument(string[] args)
        {
            string? profile = null;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--profile")
                {
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("-", StringComparison.Ordinal))
                        throw new ArgumentException("--profile needs a value: core, standard or full");
                    profile = args[++i];
                }
                else if (args[i].StartsWith("--profile=", StringComparison.Ordinal))
                {
                    profile = args[i].Substring("--profile=".Length);
                    if (string.IsNullOrWhiteSpace(profile))
                        throw new ArgumentException("--profile= needs a value: core, standard or full");
                }
                else
                {
                    throw new ArgumentException(
                        $"unknown argument '{args[i]}'. The only option is --profile <core|standard|full>.");
                }
            }
            return profile;
        }

        private static async Task ConnectAndKeepConnected(EngineConnection engine, CancellationToken ct)
        {
            try
            {
                await engine.TryDiscoverAndConnect(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                StdioTransport.LogInfo($"Initial engine discovery failed: {ex.Message}");
            }

            await RetryEngineConnection(engine, ct).ConfigureAwait(false);
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
                    if (engine.IsConnected) continue; // engine_status connected meanwhile

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
