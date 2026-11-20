using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AkerMcp.Client;
using AkerMcp.Shared.Ipc;

namespace AkerMcp.Loopback
{
    /// <summary>
    /// The contracts of the transport around the tools: what the stdio loop does with a line it cannot use, whether it
    /// keeps listening while a call runs, what the discovery directory keeps and drops, when a queued main-thread call
    /// lets its caller go, and what the pipe refuses to send.
    /// </summary>
    internal static class ServerMechanisms
    {
        public static async Task Run(Action<string, bool, string?> check)
        {
            await StdioProtocol(check);
            LockFiles(check);
            await Dispatcher(check);
            await FrameLimit(check);
        }

        // ---- The real server process, spoken to over its stdin/stdout ----
        private static async Task StdioProtocol(Action<string, bool, string?> check)
        {
            // A discovery directory of its own, empty: no engine, so a forwarded call waits out the reconnect grace,
            // which is the long call this needs.
            var tmp = Path.Combine(Path.GetTempPath(), "stdio-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);

            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(typeof(AkerMcp.Server.McpServer).Assembly.Location);
            psi.Environment["TMP"] = tmp;
            psi.Environment["TEMP"] = tmp;
            psi.Environment.Remove("AKER_MCP_ENGINE");

            using var server = Process.Start(psi)!;
            server.ErrorDataReceived += (_, _) => { };
            server.BeginErrorReadLine();
            var stdin = server.StandardInput;
            stdin.AutoFlush = true;

            var lines = Channel.CreateUnbounded<string>();
            _ = Task.Run(async () =>
            {
                string? line;
                while ((line = await server.StandardOutput.ReadLineAsync()) != null) await lines.Writer.WriteAsync(line);
                lines.Writer.TryComplete();
            });

            async Task<JsonElement?> Next(int timeoutMs)
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                try { return JsonSerializer.Deserialize<JsonElement>(await lines.Reader.ReadAsync(cts.Token)); }
                catch { return null; }
            }

            // A write to a server that has died fails the checks that follow, not the run.
            async Task Send(string line)
            {
                try { await stdin.WriteLineAsync(line); } catch (IOException) { } catch (ObjectDisposedException) { }
            }

            static int Code(JsonElement? m) =>
                m is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("error", out var e) && e.TryGetProperty("code", out var c)
                    ? c.GetInt32() : 0;
            static bool IdIsNull(JsonElement? m) =>
                m is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Null;
            static string Id(JsonElement? m) =>
                m is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("id", out var id) ? id.ToString() : "";

            try
            {
                // One malformed line used to look like end of input: the server logged it and exited.
                await Send("{\"jsonrpc\":");
                var parse = await Next(10_000);
                check("a line that is not JSON is answered -32700 with id null", Code(parse) == -32700 && IdIsNull(parse), parse?.ToString());

                await Send("{\"jsonrpc\":\"2.0\",\"id\":1.5,\"method\":\"ping\"}");
                var badId = await Next(5_000);
                check("an id that is neither a string nor an integer is -32600, not a crash", Code(badId) == -32600 && IdIsNull(badId), badId?.ToString());

                await Send("[{\"jsonrpc\":\"2.0\",\"id\":\"b1\",\"method\":\"ping\"},{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}]");
                var batch = await Next(5_000);
                check("a batch is answered with an array holding one answer per request",
                    batch is { ValueKind: JsonValueKind.Array } arr && arr.GetArrayLength() == 1 && Id(arr[0]) == "b1", batch?.ToString());

                await Send("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}");
                var alive = await Next(5_000);
                check("after all of that the server is still answering", Id(alive) == "2" && Code(alive) == 0, alive?.ToString());

                // A forwarded call with no engine waits for one (up to 20 s). The loop used to await it before reading
                // the next line: the ping below waited behind it, and the cancellation was read only after it ended.
                await Send("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"get_selection\",\"arguments\":{}}}");
                await Task.Delay(300);
                var sw = Stopwatch.StartNew();
                await Send("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"ping\"}");
                var ping = await Next(5_000);
                check("a ping is answered while a tool call is still running",
                    Id(ping) == "4" && sw.ElapsedMilliseconds < 3_000, $"{ping} after {sw.ElapsedMilliseconds} ms");

                // Cancelled: no answer for 3, and the next tool call does not wait for 3's 20 s to run out. engine_status
                // also comes without `arguments`, which the protocol allows.
                await Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":3,\"reason\":\"test\"}}");
                sw.Restart();
                await Send("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"engine_status\"}}");
                var answers = new List<string>();
                JsonElement? after;
                while ((after = await Next(8_000)) != null)
                {
                    answers.Add(Id(after));
                    if (Id(after) == "5") break;
                }
                check("a cancelled call frees the next one at once and is never answered",
                    answers.Contains("5") && !answers.Contains("3") && sw.ElapsedMilliseconds < 5_000,
                    $"answers [{string.Join(",", answers)}] after {sw.ElapsedMilliseconds} ms");

                try { stdin.Close(); } catch (IOException) { }
                var exited = server.WaitForExit(10_000);
                check("end of input ends the server cleanly", exited && server.ExitCode == 0, exited ? $"exit {server.ExitCode}" : "still running");
            }
            finally
            {
                try { if (!server.HasExited) server.Kill(entireProcessTree: true); } catch { }
                try { Directory.Delete(tmp, recursive: true); } catch { }
            }

            // A flag the server does not have is a configuration error, not something to ignore.
            var bad = new ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            bad.ArgumentList.Add(typeof(AkerMcp.Server.McpServer).Assembly.Location);
            bad.ArgumentList.Add("--profle");
            bad.ArgumentList.Add("full");
            // With stdin closed at once: a server that ignored the flag would start and then end on end of input
            // with code 0, instead of waiting for a client that never comes.
            bad.RedirectStandardInput = true;
            bad.Environment["TMP"] = tmp;
            bad.Environment["TEMP"] = tmp;
            Directory.CreateDirectory(tmp);
            using var typo = Process.Start(bad)!;
            typo.StandardInput.Close();
            var stderrTask = typo.StandardError.ReadToEndAsync();
            _ = typo.StandardOutput.ReadToEndAsync();
            typo.WaitForExit(15_000);
            var stderr = await Task.WhenAny(stderrTask, Task.Delay(2_000)) == stderrTask ? stderrTask.Result : "";
            try { if (!typo.HasExited) typo.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(tmp, recursive: true); } catch { }
            check("an unknown command-line flag exits with code 2", typo.HasExited && typo.ExitCode == 2, $"exit {(typo.HasExited ? typo.ExitCode : -1)}: {stderr}");
        }

        // ---- The discovery directory ----
        private static void LockFiles(Action<string, bool, string?> check)
        {
            var dir = LockFileStore.Directory;
            Directory.CreateDirectory(dir);
            using var self = Process.GetCurrentProcess();

            // Half a file — what a reader could see while an older plugin was still writing — is left alone while
            // it is fresh. It used to be deleted on sight, and that plugin was never found again.
            var partial = Path.Combine(dir, "partial-test.lock");
            File.WriteAllText(partial, "{\"pipe\":\"aker-mcp-x\",\"pi");
            LockFileStore.ReadLive();
            check("a lock file that does not parse yet is kept while it is fresh", File.Exists(partial), null);
            if (!File.Exists(partial)) File.WriteAllText(partial, "{\"pipe\":\"aker-mcp-x\",\"pi");
            File.SetLastWriteTimeUtc(partial, DateTime.UtcNow.AddMinutes(-5));
            LockFileStore.ReadLive();
            check("a lock file that never became readable is removed", !File.Exists(partial), null);

            // A live pid whose start time does not match is a recycled pid: the plugin that wrote it is gone.
            var recycled = Path.Combine(dir, "recycled-test.lock");
            LockFileStore.Write(recycled, new EngineLockInfo
            {
                Pipe = "aker-mcp-recycled", Engine = "Recycled", Pid = self.Id,
                StartTime = self.StartTime.ToUniversalTime().AddHours(-3).ToString("o"),
            });
            var live = LockFileStore.ReadLive();
            check("a lock whose pid was recycled is removed", !File.Exists(recycled) && !live.Exists(l => l.Engine == "Recycled"), null);

            var mine = Path.Combine(dir, "project-test.lock");
            LockFileStore.Write(mine, new EngineLockInfo
            {
                Pipe = "aker-mcp-project-test", Engine = "ProjectTest", Pid = self.Id,
                StartTime = self.StartTime.ToUniversalTime().ToString("o"),
                Project = PluginDiscovery.ProjectName(@"C:\Work\Space Game\"), ProjectPath = @"C:\Work\Space Game\",
            });
            live = LockFileStore.ReadLive();
            var found = live.Find(l => l.Engine == "ProjectTest");
            check("a lock carries the project, named after its folder", found?.Project == "Space Game", found?.Project);
            check("a solution path is named after the solution", PluginDiscovery.ProjectName(@"C:\Work\Racer\Racer.sln") == "Racer",
                PluginDiscovery.ProjectName(@"C:\Work\Racer\Racer.sln"));
            File.Delete(mine);
            check("writing a lock leaves no temporary file behind", Directory.GetFiles(dir, "*.tmp").Length == 0,
                string.Join(", ", Directory.GetFiles(dir, "*.tmp")));
        }

        // ---- The main-thread queue every adapter derives from ----
        private sealed class ManualDispatcher : MainThreadDispatcherBase
        {
            protected override void ScheduleProcessQueue() { } // pumped by hand, like an editor whose thread is busy
        }

        private static async Task Dispatcher(Action<string, bool, string?> check)
        {
            var dispatcher = new ManualDispatcher();
            var ran = false;
            using var cts = new CancellationTokenSource();
            var call = dispatcher.RunOnMainThread(() => { ran = true; return 1; }, cts.Token);
            cts.Cancel();
            var finished = await Task.WhenAny(call, Task.Delay(2_000)) == call;
            // With the main thread held (a modal dialog), the caller used to wait until the queue was pumped again.
            check("a cancelled main-thread call lets its caller go without the queue being pumped", finished && call.IsCanceled,
                finished ? call.Status.ToString() : "still waiting");
            dispatcher.ProcessQueue();
            check("a cancelled main-thread call does not run when the queue is pumped", !ran, null);

            var order = new List<string>();
            _ = dispatcher.RunOnMainThread(() =>
            {
                order.Add("first");
                _ = dispatcher.RunOnMainThread(() => { order.Add("queued meanwhile"); return 0; });
                return 0;
            });
            dispatcher.ProcessQueue();
            check("one pump runs what was queued when it began, not what arrives meanwhile", order.Count == 1, string.Join(", ", order));
            dispatcher.ProcessQueue();
            check("what arrived meanwhile runs on the next pump", order.Count == 2, string.Join(", ", order));
        }

        // ---- The pipe ----
        private static async Task FrameLimit(Action<string, bool, string?> check)
        {
            using var stream = new MemoryStream();
            using var channel = new IpcChannel(stream);
            string? error = null;
            try
            {
                await channel.SendResponse(IpcResponse.Ok(1, new byte[IpcConstants.MaxFrameBytes + 1]));
            }
            catch (IpcFrameTooLargeException ex) { error = ex.Message; }
            // The reader drops the connection on an oversized frame, and the caller then read "engine disconnected" for
            // a reply that was simply too large. Refused before a byte is written, the pipe stays usable.
            check("a message over the frame limit is refused before anything is written", error != null && stream.Length == 0,
                error ?? $"no error, {stream.Length} bytes written");

            await channel.SendResponse(IpcResponse.Ok(2, new byte[] { 1, 2, 3 }));
            stream.Position = 0;
            string next;
            try
            {
                var back = await new IpcChannel(stream).ReceiveResponse();
                next = back.Id == 2 && back.Payload?.Length == 3 ? "ok" : $"id {back.Id}";
            }
            catch (Exception ex) { next = ex.Message; }
            check("the channel still carries the next message", next == "ok", next);
        }
    }
}
