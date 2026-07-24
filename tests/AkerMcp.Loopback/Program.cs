using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AkerMcp.Server;
using AkerMcp.Shared.Protocol;

namespace AkerMcp.Loopback
{
    /// <summary>
    /// CI smoke test: runs the real client -> named-pipe -> EngineConnection -> ToolRegistry stack
    /// against a headless fake engine and asserts the runtime-loop + verification tools end to end.
    /// Exit code 0 = all passed. No editor required.
    /// </summary>
    internal static class Program
    {
        private static int _pass, _fail;

        private static void Check(string name, bool ok, string? detail = null)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}" + (ok || detail == null ? "" : "  -- " + Trim(detail)));
            if (ok) _pass++; else _fail++;
        }

        private static async Task<int> Main()
        {
            Console.WriteLine("AkerMcp loopback CI smoke test\n");

            // Isolate the discovery dir (temp/aker-mcp) from any REAL engine running on this
            // machine, so the loopback only ever finds its own fake. Both PluginDiscovery and
            // EngineConnection derive the dir from Path.GetTempPath(), which reads TMP/TEMP.
            var iso = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aker-loopback-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(iso);
            Environment.SetEnvironmentVariable("TMP", iso);
            Environment.SetEnvironmentVariable("TEMP", iso);

            var fake = new FakeEnginePlugin();
            fake.Start();
            await Task.Delay(300);

            using var engine = new EngineConnection();
            var cts = new CancellationTokenSource();
            // Background retry loop (mirrors Server/Program.cs) so WaitForConnection can reconnect.
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    if (!engine.IsConnected)
                        try { await engine.TryDiscoverAndConnect(cts.Token); } catch { }
                    try { await Task.Delay(250, cts.Token); } catch { break; }
                }
            });

            var connected = await engine.WaitForConnection(10_000, cts.Token);
            Check("connect to fake engine", connected);
            if (!connected) { Cleanup(fake, cts); return 1; }

            var reg = new ToolRegistry(engine);

            // tools/list exposes the new tools
            var names = new HashSet<string>();
            foreach (var t in reg.ListTools().Tools) names.Add(t.Name);
            foreach (var n in new[] { "enter_play", "exit_play", "get_play_state", "set_play_pause", "play_step",
                                      "capture_sequence", "send_input", "sample_state", "assert_state", "playtest",
                                      "create_sound", "add_primitive" })
                Check($"tools/list has {n}", names.Contains(n));

            // enter_play -> HandlePlayTransition settle-poll confirms the state
            var r = await Call(reg, "enter_play", new { });
            Check("enter_play ok", !r.IsError, Text(r));
            Check("enter_play confirms isPlaying:true (settle-poll)", Text(r).Contains("\"isPlaying\":true"), Text(r));

            var gs = await Call(reg, "get_play_state", new { });
            Check("get_play_state isPlaying:true", Text(gs).Contains("\"isPlaying\":true"), Text(gs));

            var sp = await Call(reg, "set_play_pause", new { paused = true });
            Check("set_play_pause isPaused:true", Text(sp).Contains("\"isPaused\":true"), Text(sp));

            var stp = await Call(reg, "play_step", new { frames = 3 });
            Check("play_step ok", !stp.IsError, Text(stp));

            var si = await Call(reg, "send_input", new { events = new[] { new { type = "key", key = "Space", hold_ms = 40 } } });
            Check("send_input ok (engine-internal)", !si.IsError, Text(si));
            Check("send_input reached the fake simulator", fake.Input.Received.Count >= 1);

            var cap = await Call(reg, "capture_sequence", new { count = 2, interval_ms = 10 });
            Check("capture_sequence returns 2 images", CountImages(cap) == 2, $"images={CountImages(cap)}; " + Text(cap));

            var sam = await Call(reg, "sample_state", new { probes = new { x = "1+1" } });
            Check("sample_state ok", !sam.IsError, Text(sam));

            var asr = await Call(reg, "assert_state", new { assertions = new[] { new { expression = "x", op = ">", value = 0 } } });
            Check("assert_state passed:true", Text(asr).Contains("\"passed\":true"), Text(asr));

            var pt = await Call(reg, "playtest", new
            {
                steps = new object[]
                {
                    new { input = new { events = new[] { new { type = "key", key = "Space" } } } },
                    new { wait_ms = 10 },
                    new { capture = true },
                    new { assert = new[] { new { expression = "y", op = ">=", value = 0 } } },
                },
                criteria = new[] { new { expression = "z", op = "truthy" } },
            });
            Check("playtest PASSED", Text(pt).Contains("PASSED"), Text(pt));
            Check("playtest returned a frame", CountImages(pt) >= 1);

            var ep = await Call(reg, "exit_play", new { });
            Check("exit_play ok", !ep.IsError, Text(ep));

            // Reconnect: simulate a plugin restart (Unity domain reload) — the retry loop reconnects.
            fake.Stop();
            await Task.Delay(500);
            Check("disconnected after plugin stop", !engine.IsConnected);
            fake.Start();
            var reconnected = await engine.WaitForConnection(10_000, cts.Token);
            Check("reconnected after plugin restart", reconnected);
            var after = await Call(reg, "get_play_state", new { });
            Check("tool works after reconnect", !after.IsError, Text(after));

            Cleanup(fake, cts);
            Console.WriteLine($"\n{_pass} passed, {_fail} failed");
            return _fail == 0 ? 0 : 1;
        }

        private static void Cleanup(FakeEnginePlugin fake, CancellationTokenSource cts)
        {
            cts.Cancel();
            try { fake.Stop(); } catch { }
        }

        private static string Text(ToolResult r) => r.Content.Count > 0 ? (r.Content[0].Text ?? "") : "";

        private static int CountImages(ToolResult r)
        {
            int n = 0;
            foreach (var c in r.Content) if (c.Type == "image") n++;
            return n;
        }

        private static string Trim(string s) => s.Length <= 100 ? s.Replace("\n", " ") : s.Substring(0, 100).Replace("\n", " ") + "…";

        private static async Task<ToolResult> Call(ToolRegistry reg, string name, object argsObj)
        {
            var el = JsonSerializer.SerializeToElement(new { name, arguments = argsObj });
            return await reg.CallTool(el, CancellationToken.None);
        }
    }
}
