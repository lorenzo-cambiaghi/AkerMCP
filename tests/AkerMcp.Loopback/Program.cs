using System;
using System.Collections.Generic;
using System.Linq;
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

            // Every tool declares all four hints. A client that sees none assumes
            // destructive + open-world and asks before each call; a half-declared
            // set used to drop every "false" from the wire.
            var incomplete = new List<string>();
            var registered = new HashSet<string>();
            foreach (var t in reg.ListTools().Tools)
            {
                registered.Add(t.Name);
                var a = t.Annotations;
                if (a == null || !a.ReadOnlyHint.HasValue || !a.DestructiveHint.HasValue
                    || !a.IdempotentHint.HasValue || !a.OpenWorldHint.HasValue)
                    incomplete.Add(t.Name);
            }
            Check("every tool declares all four annotation hints", incomplete.Count == 0, string.Join(", ", incomplete));
            var stale = new List<string>();
            foreach (var n in ToolAnnotationTable.Names) if (!registered.Contains(n)) stale.Add(n);
            Check("annotation table names only registered tools", stale.Count == 0, string.Join(", ", stale));
            var reads = 0;
            foreach (var t in reg.ListTools().Tools) if (t.Annotations?.ReadOnlyHint == true) reads++;
            Check("read-only tools are a minority the client may auto-approve", reads >= 12 && reads < registered.Count / 2, $"{reads} of {registered.Count}");

            // The handshake carries the workflow; the full playbook is a resource that
            // needs no engine.
            var hs = ServerInstructions.Handshake(registered, System.Array.Empty<string>(), "full");
            Check("handshake instructions are compact", hs.Length > 600 && hs.Length < 2500, hs.Length.ToString());
            Check("handshake names the inspect/modify/verify order and the guide", hs.Contains("inspect") && hs.Contains(ServerInstructions.GuideUri));
            var coreHs = ServerInstructions.Handshake(ToolProfiles.Core, new[] { "playtest", "build_player" }, "core");
            Check("core handshake lists the hidden tools and how to load them",
                coreHs.Contains("not loaded: playtest, build_player") && coreHs.Contains("--profile full") && !coreHs.Contains("modal dialog"));

            // Profiles: nested, complete, and what each one costs on the wire. Every
            // character of tools/list sits in the model's context on every turn; the
            // budgets are what stop the descriptions from growing back.
            Check("profiles nest: core < standard < full == registered",
                ToolProfiles.Core.Length < ToolProfiles.Standard.Length && ToolProfiles.Standard.Length < ToolProfiles.Full.Length
                && new HashSet<string>(ToolProfiles.Full).SetEquals(registered),
                $"core {ToolProfiles.Core.Length}, standard {ToolProfiles.Standard.Length}, full {ToolProfiles.Full.Length}, registered {registered.Count}");
            var wire = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
            // Measured 2026-09-06: 9,117 / 16,000 / 26,817 (before the diet the full list was 36,303
            // with no annotations and 40,466 with them). Raise a ceiling only with a reason next to it.
            var budget = new Dictionary<string, int> { ["core"] = 10_000, ["standard"] = 17_500, ["full"] = 29_500 };
            foreach (var profile in new[] { "core", "standard", "full" })
            {
                var r2 = new ToolRegistry(engine);
                var (kept, dropped) = r2.ApplyProfile(profile);
                var size = JsonSerializer.Serialize(r2.ListTools().Tools, wire).Length;
                Check($"profile {profile}: {kept.Count} tools, tools/list {size} chars within {budget[profile]}",
                    size <= budget[profile] && kept.Count + dropped.Count == registered.Count);
            }
            var (inc, _) = new ToolRegistry(engine).ApplyProfile("core", include: new[] { "playtest" }, exclude: new[] { "delete" });
            Check("include/exclude adjust a profile by name", inc.Contains("playtest") && !inc.Contains("delete"));
            var hiddenCall = await Call(new ToolRegistry(engine).Tap(r => r.ApplyProfile("core")), "playtest", new { });
            Check("calling a hidden tool names the profile and the fix", Text(hiddenCall).Contains("not loaded in tool profile 'core'") && Text(hiddenCall).Contains("--profile full"), Text(hiddenCall));
            var tooLong = new List<string>();
            foreach (var t in reg.ListTools().Tools)
            {
                if (string.IsNullOrWhiteSpace(t.Description) || t.Description.Length > 800) tooLong.Add($"{t.Name}({t.Description?.Length ?? 0})");
                if (t.InputSchema.GetRawText().Contains("\"title\":\"")) tooLong.Add($"{t.Name}(schema title)");
            }
            Check("every description is present and under 800 chars, no schema titles", tooLong.Count == 0, string.Join(", ", tooLong));
            var titledParam = reg.ListTools().Tools.First(t => t.Name == "capture_window").InputSchema.GetRawText();
            Check("a parameter named 'title' survives the title strip", titledParam.Contains("\"title\": {") || titledParam.Contains("\"title\":{"));
            var resources = new ResourceRegistry(engine);
            Check("aker://guide is listed", resources.ListResources().Resources.Exists(r => r.Uri == ServerInstructions.GuideUri));
            var guideRead = await resources.ReadResource(
                JsonSerializer.Deserialize<JsonElement>("{\"uri\":\"" + ServerInstructions.GuideUri + "\"}"), cts.Token);
            Check("aker://guide reads without forwarding to the engine",
                guideRead.Contents.Count == 1 && (guideRead.Contents[0].Text ?? "").Length > 2000
                && guideRead.Contents[0].MimeType == "text/markdown");

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

            // FrameCount must advance across reads — the "is the game loop actually live?" check.
            var s1 = await Call(reg, "get_play_state", new { });
            var s2 = await Call(reg, "get_play_state", new { });
            long f1 = ExtractLong(Text(s1), "frameCount"), f2 = ExtractLong(Text(s2), "frameCount");
            Check("frameCount advances (live loop)", f2 > f1 && f1 >= 0, $"{f1} -> {f2}");

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

            // ---- Two engines running: WHO answers must be a choice, and it must hold ----
            // The bug this covers: with two editors up, discovery took whichever lock file sorted
            // first, and every reconnect (a Unity domain reload is one) ran discovery AGAIN. The
            // target changed mid-session with no error anywhere — the tools kept working, against
            // the wrong application.
            var other = new FakeEnginePlugin("Zebra");
            other.Start();
            await Task.Delay(400);

            var listed = Text(await Call(reg, "engine_status", new { }));
            Check("engine_status lists both engines",
                listed.Contains("Loopback") && listed.Contains("Zebra"), listed);

            var pinned = Text(await Call(reg, "engine_status", new { engine = "zebra" }));
            Check("pinning switches the live target", ConnectedName(pinned) == "Zebra", pinned);

            other.Stop();
            await Task.Delay(400);
            other.Start();
            await engine.WaitForConnection(10_000, cts.Token);
            var reloaded = Text(await Call(reg, "engine_status", new { }));
            Check("pinned engine survives a reload", ConnectedName(reloaded) == "Zebra", reloaded);

            // Pinned to something that is not running: staying disconnected is the POINT. Falling
            // back to the other engine is exactly what made the failure invisible.
            await Call(reg, "engine_status", new { engine = "nosuchengine" });
            await Task.Delay(1500);   // the retry loop gets its chance to (not) connect
            Check("pinned-but-absent stays disconnected instead of falling back", !engine.IsConnected);

            await Call(reg, "engine_status", new { engine = "" });
            Check("unpinning connects again", await engine.WaitForConnection(10_000, cts.Token));

            try { other.Stop(); } catch { }
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

        private static ToolRegistry Tap(this ToolRegistry r, System.Action<ToolRegistry> f) { f(r); return r; }

        /// <summary>
        /// The engine named under "connected" — NOT anywhere in the blob. engine_status also lists
        /// every available engine, so a plain Contains("Zebra") would pass even while connected to
        /// the other one: the test would report exactly the bug it exists to catch as fixed.
        /// </summary>
        private static string ConnectedName(string json)
        {
            try
            {
                var el = JsonSerializer.Deserialize<JsonElement>(json);
                if (el.ValueKind == JsonValueKind.Object &&
                    el.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.Object &&
                    c.TryGetProperty("engine", out var name))
                    return name.GetString() ?? "";
            }
            catch { }
            return "";
        }

        private static long ExtractLong(string json, string prop)
        {
            try
            {
                var el = JsonSerializer.Deserialize<JsonElement>(json);
                if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.TryGetInt64(out var n))
                    return n;
            }
            catch { }
            return -1;
        }

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
