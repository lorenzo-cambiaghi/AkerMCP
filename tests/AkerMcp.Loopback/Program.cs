using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AkerMcp.Server;
using AkerMcp.Shared.Abstraction;
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
            // Measured 2026-09-10: 9,117 / 16,516 / 29,327 (before the diet the full list was 36,303
            // with no annotations and 40,466 with them). Raise a ceiling only with a reason next to it.
            // The full ceiling moved from 29,500 to 31,000 for ui_action: twelve actions and their
            // parameters are 2,500 characters that only a session driving the interface by hand pays,
            // which is why the tool sits in 'full' and not in 'standard'. Then to 33,000 for
            // create_music, whose channel/note grammar has to be written out — a model cannot guess
            // that "-" holds a note — and which likewise only the authoring profile pays for.
            var budget = new Dictionary<string, int> { ["core"] = 10_000, ["standard"] = 17_500, ["full"] = 33_000 };
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
            var badProfile = false;
            try { ToolProfiles.Resolve("turbo"); } catch (System.ArgumentException) { badProfile = true; }
            Check("an unknown profile name is refused", badProfile);

            // ui_action coordinates. The model points at a JPEG that was shrunk to fit 1920 px
            // and knows nothing about where the window is; the server owns both halves of the
            // conversion, so this arithmetic is the whole feature. An error here does not throw
            // — it clicks somewhere else and reports success.
            var unscaled = new CaptureAnchor
            {
                OriginX = 100, OriginY = 40,
                SourceWidth = 1600, SourceHeight = 900,
                ImageWidth = 1600, ImageHeight = 900,
            };
            Check("an unscaled capture maps a pixel to the window origin plus that pixel",
                unscaled.ToScreen(0, 0) == (100, 40) && unscaled.ToScreen(800, 450) == (900, 490),
                $"{unscaled.ToScreen(0, 0)} and {unscaled.ToScreen(800, 450)}");

            // 2560x1440 leaves the server as 1920x1080: two thirds of the way across the image
            // is two thirds of the way across the window, not 1707 px from its left edge.
            var shrunk = new CaptureAnchor
            {
                OriginX = 0, OriginY = 0,
                SourceWidth = 2560, SourceHeight = 1440,
                ImageWidth = 1920, ImageHeight = 1080,
            };
            Check("a shrunk capture undoes its own scale",
                shrunk.ToScreen(960, 540) == (1280, 720) && shrunk.ToScreen(1919, 1079) == (2559, 1439),
                $"{shrunk.ToScreen(960, 540)} and {shrunk.ToScreen(1919, 1079)}");

            // A monitor left of the primary has negative coordinates; a click there must not be
            // folded back onto screen one.
            var secondMonitor = new CaptureAnchor
            {
                OriginX = -1920, OriginY = 200,
                SourceWidth = 800, SourceHeight = 600,
                ImageWidth = 800, ImageHeight = 600,
            };
            Check("a window on a monitor left of the primary keeps its negative coordinates",
                secondMonitor.ToScreen(10, 10) == (-1910, 210), $"{secondMonitor.ToScreen(10, 10)}");

            Check("points outside the image are rejected, inside accepted",
                shrunk.Contains(0, 0) && shrunk.Contains(1919, 1079)
                && !shrunk.Contains(1920, 500) && !shrunk.Contains(-1, 0) && !shrunk.Contains(500, 1080));

            var moved = shrunk.WithOrigin(300, 150);
            Check("a moved window keeps its image, only the origin follows",
                moved.ImageWidth == shrunk.ImageWidth && moved.SourceWidth == shrunk.SourceWidth
                && moved.ToScreen(960, 540) == (1580, 870), $"{moved.ToScreen(960, 540)}");

            Check("the anchor tells the model what the pixels mean",
                shrunk.Describe().Contains("1920x1080") && shrunk.Describe().Contains("2560x1440"),
                shrunk.Describe());

            // Protocol skew. Every optional capability here is "the older peer answers
            // NOT_SUPPORTED", so a minor difference is normal operation and must not be
            // reported as breakage — a warning that cries wolf on every release is one nobody
            // reads on the release that matters.
            Check("a minor protocol difference is compatible, not a mismatch",
                !EngineConnection.DescribeVersionSkew("1.6.0", "1.7.0").breaking
                && !EngineConnection.DescribeVersionSkew("1.7.0", "1.6.0").breaking);
            Check("the same version says nothing at all",
                EngineConnection.DescribeVersionSkew("1.7.0", "1.7.0").message == null);
            Check("a major difference is a real mismatch",
                EngineConnection.DescribeVersionSkew("2.0.0", "1.7.0").breaking
                && EngineConnection.DescribeVersionSkew("nonsense", "1.7.0").breaking);

            // One version, three places: IpcConstants.ProtocolVersion and the <Version> of the two
            // packable csproj. The packages are versioned by the protocol they speak, so a drift
            // here ships a package that announces a contract it does not implement.
            static string PackageVersionOf(System.Type t)
            {
                var attrs = t.Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                var v = attrs.Length > 0
                    ? ((System.Reflection.AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion
                    : (t.Assembly.GetName().Version?.ToString() ?? "");
                return v.Split('+')[0];   // the SDK appends +<commit> when SourceLink is on
            }
            var product = ServerVersion.Product;
            Check($"the server announces a real product version ({product}), not the SDK default",
                product != "1.0.0" && System.Text.RegularExpressions.Regex.IsMatch(product, @"^\d+\.\d+\.\d+$"),
                product);
            var protocol = AkerMcp.Shared.Ipc.IpcConstants.ProtocolVersion;
            Check($"AkerMcp.Shared package version equals the IPC protocol version {protocol}",
                PackageVersionOf(typeof(AkerMcp.Shared.Ipc.IpcConstants)) == protocol, PackageVersionOf(typeof(AkerMcp.Shared.Ipc.IpcConstants)));
            Check($"AkerMcp.Client package version equals the IPC protocol version {protocol}",
                PackageVersionOf(typeof(AkerMcp.Client.EnginePluginBase)) == protocol, PackageVersionOf(typeof(AkerMcp.Client.EnginePluginBase)));
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

            // A key with neither 'pressed' nor 'hold_ms' is a tap. It used to be a press with no
            // release: the key stayed down in the game, and on the OS path on the whole desktop.
            int tapFrom = fake.Input.Received.Count;
            await Call(reg, "send_input", new
            {
                events = new object[] { new { type = "key", key = "Space" }, new { type = "key", key = "Space", pressed = true } },
            });
            var taps = fake.Input.Received.Skip(tapFrom).ToList();
            Check("a bare key is a tap, an explicit press stays a press",
                taps.Count == 2 && taps[0].HoldMs > 0 && taps[1].HoldMs == 0 && taps[1].Pressed,
                string.Join(", ", taps.Select(t => $"pressed={t.Pressed} hold={t.HoldMs}")));

            // get_console_logs: count is how many MATCHING entries come back. The handler used to cut the buffer to
            // the last `count` entries and filter afterwards, so a warning older than `count` lines was reported as
            // "(No matching log entries)" while it was still in the buffer.
            fake.Editor.Logs.Add(new LogEntry { Timestamp = DateTime.Now, Level = LogLevel.Warning, Message = "loopback warning marker" });
            for (int i = 0; i < 60; i++)
                fake.Editor.Logs.Add(new LogEntry { Timestamp = DateTime.Now, Level = LogLevel.Info, Message = $"loopback info {i}" });
            var warn = await Call(reg, "get_console_logs", new { level_filter = "warning", count = 5 });
            Check("get_console_logs filters the whole buffer before counting (level)", Text(warn).Contains("loopback warning marker"), Text(warn));
            var found = await Call(reg, "get_console_logs", new { search = "warning marker", count = 3 });
            Check("get_console_logs filters the whole buffer before counting (search)", Text(found).Contains("loopback warning marker"), Text(found));
            var tail = await Call(reg, "get_console_logs", new { count = 2 });
            Check("get_console_logs without filters still returns only the last count",
                Text(tail).Contains("loopback info 59") && Text(tail).Contains("loopback info 58") && !Text(tail).Contains("loopback info 57"), Text(tail));
            // An empty answer names the window it searched: the engines keep a few hundred entries, and a bare "no
            // matches" read as "it never happened" for a warning that had already scrolled out of the buffer.
            var none = await Call(reg, "get_console_logs", new { search = "a marker nobody logged", count = 5 });
            Check("get_console_logs says how far back an empty search went",
                Text(none).Contains($"in the last {fake.Editor.Logs.Count} console entries, since {fake.Editor.Logs[0].Timestamp:HH:mm:ss}"), Text(none));

            // Values an engine hands out as they are. UnityEngine.Vector3 has a property of its own type (normalized), and
            // System.Text.Json followed it 64 levels down: inspect failed on EVERY Unity GameObject, because every Transform
            // has a position. An enum came back as {"value__":1}.
            var probe = Text(await Call(reg, "inspect", new { target = FakeProbeNode.ProbePath }));
            Check("inspect writes a vector whose property returns its own type", probe.Contains("\"x\":1") && probe.Contains("\"z\":3"), probe);
            Check("inspect writes an enum by name", probe.Contains("\"SolidColor\""), probe);
            fake.Editor.SelectedPath = FakeProbeNode.ProbePath;
            var selection = Text(await Call(reg, "get_selection", new { }));
            fake.Editor.SelectedPath = null;
            Check("get_selection writes the same values inspect does", selection.Contains("\"x\":1") && selection.Contains("\"SolidColor\""), selection);
            var modeValue = Text(await Call(reg, "get_property", new { object_path = FakeProbeNode.ProbePath, property_path = "mode" }));
            Check("get_property writes an enum by name, not {value__}", modeValue.Contains("\"SolidColor\"") && !modeValue.Contains("value__"), modeValue);
            // Last on purpose: before the guard this one does not fail, it overflows the stack and takes the process down.
            var stampValue = Text(await Call(reg, "get_property", new { object_path = FakeProbeNode.ProbePath, property_path = "stamp" }));
            Check("a property-only struct with a property of its own type is written once, not recursed into",
                stampValue.Contains("\"Ticks\":42"), stampValue);
            var loopValue = Text(await Call(reg, "get_property", new { object_path = FakeProbeNode.ProbePath, property_path = "loop" }));
            Check("two structs that return each other are cut at the depth limit, not followed forever",
                loopValue.Contains("\"id\":7") || loopValue.Contains("\"Id\":7"), loopValue.Length > 300 ? loopValue.Substring(0, 300) : loopValue);

            // query's property_filter was in the schema and in the description, and nothing read it: the handler never
            // copied it into the filter and no engine implemented it. A query for a value the object does not have
            // answered with the object, as if it matched.
            string QueryFor(object propertyFilter) => Text(Call(reg, "query", new { property_filter = propertyFilter }).GetAwaiter().GetResult());
            var matching = QueryFor(new { mode = "SolidColor" });
            var notMatching = QueryFor(new { mode = "Off" });
            Check("query property_filter keeps an object whose value matches", matching.Contains(FakeProbeNode.ProbePath), matching);
            Check("query property_filter drops an object whose value does not match", !notMatching.Contains(FakeProbeNode.ProbePath), notMatching);
            var partialVector = QueryFor(new { position = new { y = 2 } });
            var wrongVector = QueryFor(new { position = new { y = 3 } });
            Check("query property_filter matches a struct on the members given",
                partialVector.Contains(FakeProbeNode.ProbePath) && !wrongVector.Contains(FakeProbeNode.ProbePath), partialVector + " / " + wrongVector);
            var noSuchMember = QueryFor(new { position = new { w = 1 } });
            Check("query property_filter drops an object whose struct lacks a member the filter names",
                !noSuchMember.Contains(FakeProbeNode.ProbePath), noSuchMember);
            var missing = QueryFor(new { noSuchProperty = 1 });
            Check("query property_filter drops an object without the property", !missing.Contains(FakeProbeNode.ProbePath), missing);

            // set_property looks the member's type up on the node's object; on Unity that is the GameObject, which has
            // neither "position" nor "BoxCollider.size", so the value went on as its JSON text and only Convert.ChangeType
            // read it: a vector, a colour or an enum by name could not be set at all, and a number was parsed with the
            // process culture.
            await Call(reg, "set_property", new { object_path = FakeProbeNode.ProbePath, property_path = "Position", value = new { x = 4, y = 5, z = 6 } });
            var state = FakeProbeNode.State;
            Check("set_property writes a struct from a JSON object the node's object cannot type",
                state.Position.x == 4 && state.Position.y == 5 && state.Position.z == 6, $"{state.Position.x} {state.Position.y} {state.Position.z}");
            await Call(reg, "set_property", new { object_path = FakeProbeNode.ProbePath, property_path = "Mode", value = "SolidColor" });
            Check("set_property writes an enum by name", state.Mode == FakeMode.SolidColor, state.Mode.ToString());
            var culture = System.Globalization.CultureInfo.DefaultThreadCurrentCulture;
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = new System.Globalization.CultureInfo("it-IT");
            try { await Call(reg, "set_property", new { object_path = FakeProbeNode.ProbePath, property_path = "Scale", value = 0.5 }); }
            finally { System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture; }
            Check("set_property reads a decimal point whatever the process culture (it-IT)",
                Math.Abs(state.Scale - 0.5f) < 1e-6f, state.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // The reading of JSON text stops at value types: for a reference the serializer would construct a new object,
            // and a Unity asset built with Activator is a broken one. A string for a reference member is refused.
            var asset = await Call(reg, "set_property", new { object_path = FakeProbeNode.ProbePath, property_path = "Asset", value = "someAsset" });
            Check("set_property refuses text for a reference member instead of constructing one",
                state.Asset == null && Text(asset).Contains("Cannot convert"), Text(asset));

            // A value that does not fit is an error, never a zero; reads go through the shared twin; deadlines; log levels.
            await Mechanisms.Run((name, a) => Call(reg, name, a), Check);

            // The stdio loop, the discovery directory, the main-thread queue and the pipe's frame limit.
            await ServerMechanisms.Run(Check);

            // `arguments` is optional in tools/call. Without it, every forwarded tool used to fail on an element that
            // claimed a value it did not have.
            var bare = await reg.CallTool(JsonSerializer.SerializeToElement(new { name = "get_selection" }), CancellationToken.None);
            Check("a forwarded tool called without `arguments` still reaches the engine", !bare.IsError, Text(bare));

            // execute waits as long as the caller asked. The server used to give up at its fixed request deadline (30 s)
            // whatever timeout_ms said: a longer script answered "timed out after 30s" while the engine was still working.
            // The deadline is shortened here so the bench does not wait thirty real seconds; the ratio is the same.
            engine.DefaultRequestTimeoutMs = 1_000;
            fake.Executor.DelayMs = 1_500;
            var patient = await Call(reg, "execute", new { code = "return 1;", timeout_ms = 3_000 });
            Check("execute honours a timeout_ms longer than the default request deadline",
                !patient.IsError && Text(patient).Contains("\"success\":true"), Text(patient));
            var impatient = await Call(reg, "execute", new { code = "return 1;" });
            Check("without timeout_ms the default request deadline still applies",
                impatient.IsError && Text(impatient).Contains("timed out"), Text(impatient));
            fake.Executor.DelayMs = 0;
            await Task.Delay(700);   // let the abandoned script finish before the next call
            engine.DefaultRequestTimeoutMs = 30_000;

            var cap = await Call(reg, "capture_sequence", new { count = 2, interval_ms = 10 });
            Check("capture_sequence returns 2 images", CountImages(cap) == 2, $"images={CountImages(cap)}; " + Text(cap));

            // Consent. The tools that reach around the engine to the OS are the ones an
            // in-process gate cannot see, so this is where a revoked session used to keep
            // working: the adapter returned nothing, the server read that as "cannot" rather
            // than "will not", and photographed the window itself.
            var callsBefore = fake.Consent.Calls;
            var lw = await Call(reg, "list_windows", new { });
            Check("list_windows works while consent is granted", !Text(lw).Contains("refused"), Text(lw));
            for (int i = 0; i < 4; i++) await Call(reg, "list_windows", new { });
            Check("a granted answer is cached, not asked once per call",
                fake.Consent.Calls - callsBefore <= 2, $"{fake.Consent.Calls - callsBefore} asks for 5 calls");

            fake.Consent.Granted = false;
            await Task.Delay(5200);   // let the cached answer go stale

            var lwDenied = await Call(reg, "list_windows", new { });
            Check("list_windows is refused once the session is revoked",
                lwDenied.IsError && Text(lwDenied).Contains("revoked the session"), Text(lwDenied));

            var cwDenied = await Call(reg, "capture_window", new { title = "anything" });
            Check("capture_window is refused too", cwDenied.IsError && Text(cwDenied).Contains("revoked"), Text(cwDenied));

            var fwDenied = await Call(reg, "focus_window", new { title = "anything" });
            Check("focus_window is refused too", fwDenied.IsError && Text(fwDenied).Contains("revoked"), Text(fwDenied));

            var uiDenied = await Call(reg, "ui_action", new { action = "screenshot", window_title = "anything" });
            Check("ui_action is refused too", uiDenied.IsError && Text(uiDenied).Contains("revoked"), Text(uiDenied));

            var siDenied = await Call(reg, "send_input", new
            {
                events = new[] { new { type = "key", key = "Space" } },
                coords = "image",   // forces the OS-level path, where the refusal has to hold
            });
            Check("send_input's OS-level path is refused too",
                siDenied.IsError && Text(siDenied).Contains("revoked"), Text(siDenied));

            // A named window takes the OS-level path, never the engine's: the in-process path can
            // reach no window but the game view, and Unity used to take a click meant for one of the
            // editor's floating windows, put it in the Game View and answer success. Checked with
            // the session revoked, so that the routing shows (refused, and the fake simulator got
            // nothing) without any real input reaching this machine.
            int inputsBefore = fake.Input.Received.Count;
            var siNamed = await Call(reg, "send_input", new
            {
                events = new[] { new { type = "key", key = "Space" } },
                window_title = "anything",
            });
            Check("send_input with window_title goes OS-level, not in-process",
                siNamed.IsError && Text(siNamed).Contains("revoked") && fake.Input.Received.Count == inputsBefore,
                $"{Text(siNamed)} (engine got {fake.Input.Received.Count - inputsBefore})");

            fake.Consent.Granted = true;
            await Task.Delay(5200);
            var lwAgain = await Call(reg, "list_windows", new { });
            Check("granting it again lets the tools through",
                !Text(lwAgain).Contains("refused"), Text(lwAgain));

            // Synthetic input goes to whatever is in front. A target that could not be focused was
            // noted in the answer and typed at anyway — into the terminal, the IDE, the browser.
            // (A Shift tap, so a regression costs this machine no more than a modifier blip.)
            var siLost = await Call(reg, "send_input", new
            {
                events = new[] { new { type = "key", key = "Shift", hold_ms = 10 } },
                window_title = "aker-loopback-no-such-window-" + Guid.NewGuid().ToString("N"),
            });
            Check("send_input refuses when its window cannot be focused, and sends nothing",
                siLost.IsError && Text(siLost).Contains("Input not sent"), Text(siLost));

            // "No policy here" arrives in three different sentences and only one of them was
            // being read. A plugin older than the method answers "Unknown method: get_consent",
            // never the word NOT_SUPPORTED — so every OS-level call kept paying an IPC round
            // trip for the whole session, invisibly, because the fallback is permissive.
            // ---- sfxr ---------------------------------------------------------------
            // "Sounds better" is not testable, but the things that make it better are. Two
            // cheap measures do the work: the zero-crossing rate stands in for brightness
            // (a hiss crosses zero constantly, a rumble rarely), and RMS over a slice stands
            // in for the envelope's shape.
            static short[] Render(string json)
            {
                var wav = SoundSynthesizer.RenderToWav(JsonSerializer.Deserialize<JsonElement>(json));
                var pcm = new short[(wav.Length - 44) / 2];
                for (int i = 0; i < pcm.Length; i++) pcm[i] = BitConverter.ToInt16(wav, 44 + i * 2);
                return pcm;
            }
            static double Zcr(short[] pcm)
            {
                int crossings = 0;
                for (int i = 1; i < pcm.Length; i++)
                    if ((pcm[i - 1] < 0) != (pcm[i] < 0)) crossings++;
                return pcm.Length > 1 ? (double)crossings / pcm.Length : 0;
            }
            static double Rms(short[] pcm, double from, double to)
            {
                int a = (int)(pcm.Length * from), b = (int)(pcm.Length * to);
                if (b <= a) return 0;
                double sum = 0;
                for (int i = a; i < b; i++) sum += (double)pcm[i] * pcm[i];
                return Math.Sqrt(sum / (b - a));
            }

            // The bug that made every impact sound the same: the noise oscillator ignored
            // frequency, so this ratio used to be 1.0 whatever was asked for.
            var noiseLow = Zcr(Render("{\"wave\":\"noise\",\"freq\":200,\"duration\":0.4}"));
            var noiseHigh = Zcr(Render("{\"wave\":\"noise\",\"freq\":4000,\"duration\":0.4}"));
            Check("noise takes a pitch: 4000 Hz is far brighter than 200 Hz",
                noiseHigh > noiseLow * 3, $"zcr {noiseLow:0.0000} vs {noiseHigh:0.0000}");

            // The single biggest reason a raw square is unpleasant: every harmonic up to Nyquist.
            var openSquare = Zcr(Render("{\"wave\":\"square\",\"freq\":800,\"duration\":0.3}"));
            var filtered = Zcr(Render("{\"wave\":\"square\",\"freq\":800,\"duration\":0.3,\"lowpass\":900}"));
            Check("a low-pass takes the edge off", filtered < openSquare,
                $"zcr {openSquare:0.0000} open vs {filtered:0.0000} filtered");

            var noPunch = Rms(Render("{\"wave\":\"square\",\"freq\":440,\"duration\":0.4}"), 0, 0.15);
            var withPunch = Rms(Render("{\"wave\":\"square\",\"freq\":440,\"duration\":0.4,\"punch\":0.8}"), 0, 0.15);
            Check("punch puts the energy at the start", withPunch > noPunch * 1.1,
                $"rms {noPunch:0} vs {withPunch:0}");

            var linearTail = Rms(Render("{\"wave\":\"square\",\"freq\":440,\"duration\":0.4,\"decay\":0.3,\"decay_curve\":\"linear\"}"), 0.6, 0.9);
            var expTail = Rms(Render("{\"wave\":\"square\",\"freq\":440,\"duration\":0.4,\"decay\":0.3}"), 0.6, 0.9);
            Check("the default decay is exponential, so the tail dies sooner than linear",
                expTail < linearTail, $"rms tail {expTail:0} exp vs {linearTail:0} linear");

            var flat = Zcr(Render("{\"wave\":\"square\",\"freq\":600,\"duration\":0.3}"));
            var thin = Zcr(Render("{\"wave\":\"square\",\"freq\":600,\"duration\":0.3,\"duty\":0.1}"));
            Check("duty cycle changes the tone without changing the pitch",
                Math.Abs(thin - flat) < flat * 0.25 && thin > 0, $"zcr {flat:0.0000} vs {thin:0.0000}");

            var plain = Render("{\"wave\":\"square\",\"freq\":300,\"duration\":0.5}");
            var arped = Render("{\"wave\":\"square\",\"freq\":300,\"duration\":0.5,\"arpeggio\":2.0,\"arpeggio_delay\":0.1}");
            Check("arpeggio lifts the pitch partway through",
                Zcr(arped) > Zcr(plain) * 1.4, $"zcr {Zcr(plain):0.0000} vs {Zcr(arped):0.0000}");

            var once = Rms(Render("{\"wave\":\"square\",\"freq\":500,\"duration\":0.6,\"decay\":0.1}"), 0.7, 1.0);
            var repeated = Rms(Render("{\"wave\":\"square\",\"freq\":500,\"duration\":0.6,\"decay\":0.1,\"repeat\":8}"), 0.7, 1.0);
            Check("repeat retriggers the envelope instead of leaving silence",
                repeated > once * 2, $"rms late {once:0} vs {repeated:0}");

            // ---- chiptune -----------------------------------------------------------
            static short[] Music(string json)
            {
                var wav = MusicSequencer.RenderToWav(JsonSerializer.Deserialize<JsonElement>(json));
                var pcm = new short[(wav.Length - 44) / 2];
                for (int i = 0; i < pcm.Length; i++) pcm[i] = BitConverter.ToInt16(wav, 44 + i * 2);
                return pcm;
            }

            Check("A4 is 440 Hz and the octaves and accidentals follow from it",
                MusicSequencer.TryNoteToFrequency("A4", out var a4) && Math.Abs(a4 - 440) < 0.01
                && MusicSequencer.TryNoteToFrequency("A5", out var a5) && Math.Abs(a5 - 880) < 0.01
                && MusicSequencer.TryNoteToFrequency("C4", out var c4) && Math.Abs(c4 - 261.63) < 0.05
                && MusicSequencer.TryNoteToFrequency("Bb4", out var bb) && Math.Abs(bb - 466.16) < 0.05
                && !MusicSequencer.TryNoteToFrequency("H4", out _));

            // Tempo has to reach the length of the audio, or the score is decorative.
            var atCrotchet = Music("{\"bpm\":120,\"steps_per_beat\":4,\"channels\":[{\"instrument\":\"lead\",\"notes\":\"C4 D4 E4 F4\"}]}");
            var atDouble = Music("{\"bpm\":240,\"steps_per_beat\":4,\"channels\":[{\"instrument\":\"lead\",\"notes\":\"C4 D4 E4 F4\"}]}");
            Check("twice the tempo is about half the audio", atDouble.Length < atCrotchet.Length,
                $"{atCrotchet.Length} vs {atDouble.Length} samples");

            var oneBar = Music("{\"bpm\":120,\"channels\":[{\"instrument\":\"lead\",\"notes\":\"C4 D4 E4 F4\"}]}");
            var twice = Music("{\"bpm\":120,\"repeat\":2,\"channels\":[{\"instrument\":\"lead\",\"notes\":\"C4 D4 E4 F4\"}]}");
            // Measured against the musical part only: both carry the same half-second tail for
            // the last note's decay, and at this tempo that tail is longer than the bar itself,
            // so a ratio over the whole file says almost nothing.
            const int tailSamples = 44100 / 2;
            Check("repeat plays the score exactly twice",
                twice.Length - tailSamples == 2 * (oneBar.Length - tailSamples),
                $"{oneBar.Length - tailSamples} vs {twice.Length - tailSamples} samples of music");

            // A hold must sound as one long note, not silence: the naive reading of "-" is to
            // play nothing, which turns a melody into a stutter.
            var held = Music("{\"bpm\":60,\"channels\":[{\"instrument\":\"pad\",\"notes\":\"C4 - - -\"}]}");
            double lateEnergy = Rms(held, 0.5, 0.8);
            Check("a held note keeps sounding through the steps it spans", lateEnergy > 500,
                $"rms {lateEnergy:0}");

            var quiet = Music("{\"bpm\":120,\"volume\":0.2,\"channels\":[{\"instrument\":\"lead\",\"notes\":\"C4 E4 G4 C5\"}]}");
            var loud = Music("{\"bpm\":120,\"volume\":0.9,\"channels\":[{\"instrument\":\"lead\",\"notes\":\"C4 E4 G4 C5\"}]}");
            Check("master volume scales the mix", Rms(loud, 0, 1) > Rms(quiet, 0, 1) * 2,
                $"rms {Rms(quiet, 0, 1):0} vs {Rms(loud, 0, 1):0}");

            // Four channels summed overshoot almost always; a clipped chiptune is exactly the
            // harshness this work set out to remove.
            var full = Music("{\"bpm\":120,\"channels\":[" +
                "{\"instrument\":\"lead\",\"notes\":\"C5 E5 G5 C6\"}," +
                "{\"instrument\":\"bass\",\"notes\":\"C3*4\"}," +
                "{\"instrument\":\"pad\",\"notes\":\"E4*4\"}," +
                "{\"instrument\":\"drums\",\"notes\":\"k s k s\"}]}");
            int clipped = 0;
            foreach (var s in full) if (s >= short.MaxValue - 1 || s <= short.MinValue + 1) clipped++;
            Check("a four-channel mix is normalised, not clipped", clipped < full.Length / 200,
                $"{clipped} clipped of {full.Length}");

            var drums = Music("{\"bpm\":120,\"channels\":[{\"instrument\":\"drums\",\"notes\":\"k . s . h . o .\"}]}");
            Check("the drum alphabet makes sound", Rms(drums, 0, 1) > 200, $"rms {Rms(drums, 0, 1):0}");

            var kick = Music("{\"bpm\":120,\"channels\":[{\"instrument\":\"drums\",\"notes\":\"k k k k\"}]}");
            var hats = Music("{\"bpm\":120,\"channels\":[{\"instrument\":\"drums\",\"notes\":\"h h h h\"}]}");
            Check("a kick is low and a hat is bright", Zcr(hats) > Zcr(kick) * 3,
                $"zcr {Zcr(kick):0.0000} kick vs {Zcr(hats):0.0000} hat");

            // ---- the SoundFont engine ------------------------------------------------
            // The font is 151 MB and not in the repository, so CI renders chiptune only. What
            // is worth pinning here is the behaviour when it is absent: a score must fail with
            // an instruction, not a stack trace, and must never silently come out as chiptune
            // when real instruments were asked for — that would be a placeholder that changes
            // timbre depending on which machine rendered it.
            var previousFont = Environment.GetEnvironmentVariable(SoundFontLibrary.PathVariable);
            var previousUrl = Environment.GetEnvironmentVariable(SoundFontLibrary.UrlVariable);
            try
            {
                Environment.SetEnvironmentVariable(SoundFontLibrary.PathVariable, "/definitely/not/here.sf2");
                string message = "";
                try { Music("{\"engine\":\"soundfont\",\"channels\":[{\"instrument\":\"piano\",\"notes\":\"C4\"}]}"); }
                catch (Exception ex) { message = ex.Message; }

                Check("a missing SoundFont says which switch to flip, and names the alternative",
                    message.Contains(SoundFontLibrary.PathVariable) && message.Contains("chiptune"), message);

                // The other half of the same promise: an unreachable download is refused rather
                // than quietly substituted.
                Environment.SetEnvironmentVariable(SoundFontLibrary.PathVariable, null);
                Environment.SetEnvironmentVariable(SoundFontLibrary.UrlVariable,
                    "https://127.0.0.1:9/aker-no-such-host.sf2");
                if (!SoundFontLibrary.IsAvailableLocally())
                {
                    message = "";
                    try { Music("{\"engine\":\"soundfont\",\"channels\":[{\"instrument\":\"piano\",\"notes\":\"C4\"}]}"); }
                    catch (Exception ex) { message = ex.Message; }
                    Check("an unreachable font is an error, never a silent fallback to chiptune",
                        message.Contains("No SoundFont available"), message);
                }
                else
                {
                    Check("an unreachable font is an error, never a silent fallback to chiptune",
                        true, "(skipped: a font is cached on this machine)");
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(SoundFontLibrary.PathVariable, previousFont);
                Environment.SetEnvironmentVariable(SoundFontLibrary.UrlVariable, previousUrl);
            }

            Check("the published SoundFont is pinned by hash, so a mirror cannot swap it",
                SoundFontLibrary.ExpectedSha256.Length == 64
                && SoundFontLibrary.DefaultUrl.EndsWith(SoundFontLibrary.FileName, StringComparison.Ordinal));

            // The notice written beside a downloaded font comes from a resource embedded out of
            // docs/: a renamed file or a dropped csproj line would leave the copy without it, and
            // nothing else would say so.
            using (var notice = typeof(SoundFontLibrary).Assembly.GetManifestResourceStream(SoundFontLibrary.LicenceFileName))
            {
                var text = notice == null ? "" : new System.IO.StreamReader(notice).ReadToEnd();
                Check("the server carries the FluidR3 licence notice it writes beside the font",
                    text.Contains("MIT License") && text.Contains(SoundFontLibrary.ExpectedSha256),
                    notice == null ? "resource missing" : $"{text.Length} chars");
            }

            Check("an old spec still renders the length it asks for",
                Render("{\"wave\":\"saw\",\"freq\":1200,\"freq_sweep\":-3000,\"duration\":0.25}").Length == 11025);

            // ---- the mechanisms under the audio --------------------------------------
            // Contracts, not taste: where notes land, what a filter passes relative to what it
            // stops, what a score is refused for. Nothing here pins how anything sounds.
            static JsonElement J(string json) => JsonSerializer.Deserialize<JsonElement>(json);
            static string Fails(Action act) { try { act(); return ""; } catch (Exception ex) { return ex.Message; } }
            static double Energy(short[] p, long from, long to)
            {
                double sum = 0; long n = 0;
                for (long i = from; i < to && i < p.Length; i++, n++) sum += (double)p[i] * p[i];
                return n > 0 ? Math.Sqrt(sum / n) : 0;
            }

            // A high-pass passes what is far above its cutoff and stops what is far below. It used
            // to integrate instead — boosting the lows until they clipped — and behind a low-pass
            // it was a plain volume cut. Each ratio is against the same spec without the high-pass.
            double HighpassPasses(int hz, string lowpass)
            {
                string spec = $"{{\"wave\":\"sine\",\"freq\":{hz},\"duration\":0.5,\"attack\":0,\"sustain\":1,\"decay\":0{lowpass}";
                return Rms(Render(spec + ",\"highpass\":3000}"), 0.2, 0.9) / Math.Max(1, Rms(Render(spec + "}"), 0.2, 0.9));
            }
            double openLow = HighpassPasses(100, ""), openHigh = HighpassPasses(9000, "");
            double lpLow = HighpassPasses(100, ",\"lowpass\":12000"), lpHigh = HighpassPasses(9000, ",\"lowpass\":12000");
            Check("highpass stops a tone far below its cutoff and passes one far above, with or without a low-pass",
                openLow < openHigh * 0.25 && lpLow < lpHigh * 0.25,
                $"passed below/above: open {openLow:0.00}/{openHigh:0.00}, behind a low-pass {lpLow:0.00}/{lpHigh:0.00}");

            // A loop is exactly its music, steps x repeat x the step grid: a tail inside it put a
            // gap at every loop point.
            int grid = (int)(60.0 / 120 / 4 * 44100);
            var loopWav = MusicSequencer.RenderToWav(J("{\"bpm\":120,\"repeat\":2,\"channels\":[" +
                "{\"instrument\":\"lead\",\"notes\":\"C4 E4 G4 C5 - - . .\"},{\"instrument\":\"drums\",\"notes\":\"k h s h\"}]}"), loop: true);
            int dataBytes = BitConverter.ToInt32(loopWav, 40);
            Check("a looped score is exactly steps x repeat x step long", dataBytes == 8 * 2 * grid * 2,
                $"{dataBytes / 2} samples, expected {8 * 2 * grid}");

            // The tool has to hand 'loop' to the renderer as well as to the importer: given only to
            // the importer, a looping source played the tail too, a gap at every loop point.
            var musicCall = await Call(reg, "create_music", new
            {
                name = "loop-wiring",
                loop = true,
                spec = new
                {
                    bpm = 120, repeat = 2,
                    channels = new object[]
                    {
                        new { instrument = "lead", notes = "C4 E4 G4 C5 - - . ." },
                        new { instrument = "drums", notes = "k h s h" },
                    },
                },
            });
            var imported = fake.Sound.Last;
            int importedBytes = imported == null ? -1 : BitConverter.ToInt32(imported.Wav, 40);
            Check("create_music with loop:true imports a clip exactly as long as its loop",
                !musicCall.IsError && imported != null && imported.Loop && importedBytes == 8 * 2 * grid * 2,
                $"{Text(musicCall)} ({importedBytes / 2} samples, expected {8 * 2 * grid})");
            Check("the WAV header agrees with the data it carries",
                BitConverter.ToInt32(loopWav, 4) == loopWav.Length - 8 && dataBytes == loopWav.Length - 44
                && BitConverter.ToInt32(loopWav, 24) == 44100 && BitConverter.ToInt32(loopWav, 28) == 44100 * 2
                && BitConverter.ToInt16(loopWav, 32) == 2 && BitConverter.ToInt16(loopWav, 34) == 16, $"{loopWav.Length} bytes");

            // repeat plays the whole score again: a short part comes back with the long one, not
            // straight after its own end.
            var offsets = Music("{\"bpm\":120,\"repeat\":2,\"channels\":[" +
                "{\"instrument\":\"lead\",\"notes\":\". . . . . . . . . . . . . . . .\"},{\"instrument\":\"bass\",\"notes\":\"C3*4\"}]}");
            double secondPass = Energy(offsets, 17L * grid, 19L * grid), afterFirst = Energy(offsets, 5L * grid, 7L * grid);
            Check("repeat brings a short part back with the whole score, not after its own end",
                secondPass > 0 && afterFirst < secondPass * 0.1, $"rms steps 5-7 {afterFirst:0} vs 17-19 {secondPass:0}");

            // The SoundFont timeline, without the font. Two parts ending on a shared note put two
            // note-offs on one instant, and the old ordering made List.Sort throw on exactly that.
            var duet = MusicSequencer.Score.Parse(J("{\"engine\":\"soundfont\",\"repeat\":2,\"channels\":[" +
                "{\"instrument\":\"flute\",\"notes\":\"C5 E5 G5 E5 D5 B4 C5*4\"}," +
                "{\"instrument\":\"bass\",\"notes\":\"C3 C3 G2 G2 G2 G2 C3*4\"}]}"), loop: false);
            string timelineError = "";
            List<SoundFontRenderer.NoteEvent>? timeline = null;
            try { timeline = SoundFontRenderer.Timeline(duet); } catch (Exception ex) { timelineError = ex.Message; }
            Check("the SoundFont timeline orders a duet that ends together, note-offs first at one instant",
                timeline != null && timeline.Zip(timeline.Skip(1)).All(p => p.First.Sample < p.Second.Sample
                    || (p.First.Sample == p.Second.Sample && (!p.First.On || p.Second.On))), timelineError);

            var band = MusicSequencer.Score.Parse(J("{\"engine\":\"soundfont\",\"repeat\":2,\"channels\":[" +
                "{\"instrument\":\"flute\",\"notes\":\"C5 E5 G5 E5 D5 B4 C5*4\"},{\"instrument\":\"drums\",\"notes\":\"k s\"}]}"), loop: false);
            var bandHits = SoundFontRenderer.Timeline(band).Where(e => e.On && e.Channel == 9).Select(e => e.Sample).ToList();
            Check("the SoundFont timeline repeats a short part with the whole score",
                bandHits.Contains(band.Steps * band.StepSamples) && !bandHits.Contains(2 * band.StepSamples),
                $"drum hits at {string.Join(", ", bandHits)} (grid {band.StepSamples})");

            // Refused before its buffer exists: a clip over the engine link's 64 MB frame used to
            // drop the connection, and a long enough score asked for gigabytes first.
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var oversized = Fails(() => MusicSequencer.RenderToWav(J("{\"bpm\":20,\"steps_per_beat\":1,\"sample_rate\":48000," +
                "\"channels\":[{\"instrument\":\"pad\",\"notes\":\"C4*64 C4*64 C4*64 C4*64\"}]}")));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Check("a score over the size limit is refused before it is rendered",
                oversized.Contains("too long") && allocated < 8L * 1024 * 1024, $"{allocated / 1024} KB allocated: {oversized}");

            var badTokens = Fails(() => Music("{\"channels\":[{\"instrument\":\"lead\",\"notes\":\"C4 H4 C5x E4\"}]}"));
            Check("a token that is not a note is an error that names it",
                badTokens.Contains("'H4'") && badTokens.Contains("'C5x'") && !badTokens.Contains("'E4'")
                && !MusicSequencer.TryNoteToFrequency("C5,", out _), badTokens);

            var badInstrument = Fails(() => Music("{\"channels\":[{\"instrument\":\"kazoo\",\"notes\":\"C4\"}]}"));
            var badEngine = Fails(() => Music("{\"engine\":\"fm\",\"channels\":[{\"instrument\":\"lead\",\"notes\":\"C4\"}]}"));
            Check("an unknown instrument or engine is an error that lists the valid ones",
                badInstrument.Contains("'kazoo'") && badInstrument.Contains("pluck")
                && badEngine.Contains("'fm'") && badEngine.Contains("soundfont"), $"{badInstrument} | {badEngine}");

            // '-' continues the slot before it: after a rest that is more rest, never the note
            // before the rest reaching over it.
            var restThenHold = Music("{\"bpm\":60,\"steps_per_beat\":1,\"channels\":[{\"instrument\":\"pad\",\"notes\":\"C4 . -\"}]}");
            double noteSlot = Energy(restThenHold, 0, 44100), restSlot = Energy(restThenHold, 52920, 88200);
            Check("a rest followed by a hold stays silent", restSlot < noteSlot * 0.05,
                $"rms note {noteSlot:0} vs rest {restSlot:0}");

            // A drum hit is its own envelope whatever the grid: it used to be stretched to the
            // step's length, the same hat four times longer at one step per beat than at four.
            static int LastAudible(short[] p) { int last = 0; for (int i = 0; i < p.Length; i++) if (Math.Abs((int)p[i]) > 300) last = i; return last; }
            int hatFine = LastAudible(Music("{\"bpm\":120,\"steps_per_beat\":4,\"channels\":[{\"instrument\":\"drums\",\"notes\":\"h\"}]}"));
            int hatCoarse = LastAudible(Music("{\"bpm\":120,\"steps_per_beat\":1,\"channels\":[{\"instrument\":\"drums\",\"notes\":\"h\"}]}"));
            Check("a drum hit lasts as long whatever the step grid", Math.Abs(hatFine - hatCoarse) < 441,
                $"{hatFine} vs {hatCoarse} samples");

            // The fetch, without the network: a file that is not the pinned one is refused and
            // leaves nothing behind, and a server that goes quiet after its headers is given up
            // on rather than waited for — HttpClient's own timeout stops counting at the headers.
            if (!SoundFontLibrary.IsAvailableLocally())
            {
                const string piano = "{\"engine\":\"soundfont\",\"channels\":[{\"instrument\":\"piano\",\"notes\":\"C4\"}]}";
                var urlBefore = Environment.GetEnvironmentVariable(SoundFontLibrary.UrlVariable);
                var idleBefore = SoundFontLibrary.IdleTimeout;
                var bogusFont = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "not-the-pinned-font.sf2");
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                using var hangUp = new CancellationTokenSource();
                try
                {
                    System.IO.File.WriteAllBytes(bogusFont, new byte[5 * 1024 * 1024]);
                    Environment.SetEnvironmentVariable(SoundFontLibrary.UrlVariable, new Uri(bogusFont).AbsoluteUri);
                    var refused = Fails(() => Music(piano));
                    Check("a font that is not the pinned file is refused and leaves no .part",
                        refused.Contains("SHA-256") && !System.IO.File.Exists(SoundFontLibrary.PartPath)
                        && !SoundFontLibrary.IsAvailableLocally(), refused);

                    listener.Start();
                    _ = Task.Run(async () =>
                    {
                        using var client = await listener.AcceptTcpClientAsync();
                        var stream = client.GetStream();
                        await stream.ReadAsync(new byte[4096]);
                        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Length: 10000000\r\n\r\n"));
                        await stream.WriteAsync(new byte[1000]);
                        try { await Task.Delay(TimeSpan.FromSeconds(20), hangUp.Token); } catch (OperationCanceledException) { }
                    });
                    SoundFontLibrary.IdleTimeout = TimeSpan.FromSeconds(1);
                    Environment.SetEnvironmentVariable(SoundFontLibrary.UrlVariable,
                        $"http://127.0.0.1:{((System.Net.IPEndPoint)listener.LocalEndpoint).Port}/quiet.sf2");
                    var stalled = Fails(() => Music(piano));
                    Check("a download that stalls is abandoned and leaves no .part",
                        stalled.Contains("stalled") && !System.IO.File.Exists(SoundFontLibrary.PartPath), stalled);
                }
                finally
                {
                    hangUp.Cancel();
                    listener.Stop();
                    SoundFontLibrary.IdleTimeout = idleBefore;
                    Environment.SetEnvironmentVariable(SoundFontLibrary.UrlVariable, urlBefore);
                    try { System.IO.File.Delete(bogusFont); } catch { /* temp */ }
                }
            }
            else
            {
                Check("the fetch checks (skipped: a font is cached on this machine)", true);
            }

            Check("an engine with no IAgentConsent is recognised, however it says so",
                ConsentGate.IsNoPolicyAnswer("NOT_SUPPORTED: whatever")
                && ConsentGate.IsNoPolicyAnswer("Engine does not implement IAgentConsent; ...")
                && ConsentGate.IsNoPolicyAnswer("InvalidOperationException: Unknown method: get_consent"));
            Check("a transient failure is not mistaken for an absent policy",
                !ConsentGate.IsNoPolicyAnswer("[ENGINE_DISCONNECTED] the pipe dropped")
                && !ConsentGate.IsNoPolicyAnswer("TimeoutException: the engine did not answer")
                && !ConsentGate.IsNoPolicyAnswer("NullReferenceException in the adapter"));
            // An adapter's GetConsent that throws names itself in the stack frame the plugin
            // appends; that frame must not read as "no policy", or one exception switches the
            // engine's revocations off for the whole connection.
            Check("an adapter whose GetConsent threw still has a policy",
                !ConsentGate.IsNoPolicyAnswer(
                    "NullReferenceException: Object reference not set to an instance of an object. " +
                    "(at MyEditor.Bridge.AkerMcp.Shared.Abstraction.IAgentConsent.GetConsent () [0x00012])")
                && !ConsentGate.IsNoPolicyAnswer(
                    "InvalidOperationException: not ready (at MyEditor.UiAgentConsent.GetConsent ())"));

            var sam = await Call(reg, "sample_state", new { probes = new { x = "1+1" } });
            Check("sample_state ok", !sam.IsError, Text(sam));

            var asr = await Call(reg, "assert_state", new { assertions = new[] { new { expression = "x", op = ">", value = 0 } } });
            Check("assert_state passed:true", Text(asr).Contains("\"passed\":true"), Text(asr));

            // The probes of one call are read in one execute: one compile, and every value from the same frame. A probe
            // ending in `;` is the expression it obviously is.
            int before = fake.Executor.Scripts.Count;
            var two = await Call(reg, "sample_state", new { probes = new { x = "Find(\"Bird\").x", y = "Find(\"Bird\").y;" } });
            var sent = fake.Executor.Scripts.Skip(before).ToList();
            Check("two probes are read in one execute", sent.Count == 1 && sent[0].Contains(".x") && sent[0].Contains(".y"),
                $"{sent.Count} scripts: {string.Join(" | ", sent)}");
            Check("a probe's trailing ';' is dropped", sent.Count == 1 && !sent[0].Contains(".y;"), sent.FirstOrDefault());
            Check("each probe of a batch gets its own value", Text(two).Contains("\"name\":\"x\",\"value\":\"1\"") && Text(two).Contains("\"name\":\"y\",\"value\":\"1\""), Text(two));

            // An operator that does not exist is an error now, not a false that polls until the timeout.
            var swOp = System.Diagnostics.Stopwatch.StartNew();
            var badOp = await Call(reg, "assert_state", new { assertions = new[] { new { expression = "x", op = "eq", value = 1 } }, timeout_ms = 3000 });
            Check("an unknown assertion op is reported and not polled", Text(badOp).Contains("unknown op") && Text(badOp).Contains("eq") && swOp.ElapsedMilliseconds < 2000,
                $"{swOp.ElapsedMilliseconds} ms: {Text(badOp)}");
            var loose = await Call(reg, "assert_state", new { assertions = new object[] { new { expression = "x", op = "approx", value = 1.05, tolerance = 0.1 } } });
            Check("approx honours a tolerance", Text(loose).Contains("\"passed\":true"), Text(loose));
            var tight = await Call(reg, "assert_state", new { assertions = new object[] { new { expression = "x", op = "approx", value = 1.05 } } });
            Check("approx without a tolerance keeps 1e-3", Text(tight).Contains("\"passed\":false"), Text(tight));
            // A value formatted with a decimal comma is not read as a thousands separator: "0,5" is not 5.
            fake.Executor.Value = "0,5";
            var comma = await Call(reg, "assert_state", new { assertions = new object[] { new { expression = "x", op = ">", value = 4 } } });
            Check("\"0,5\" is not read as 5", Text(comma).Contains("\"passed\":false"), Text(comma));
            fake.Executor.Value = "1";

            // A playtest step with two actions, or a key that is no action, is refused before play starts; each used
            // to drop the part it did not recognise.
            var twoInOne = await Call(reg, "playtest", new { steps = new object[] { new { wait_ms = 10, sample = new { y = "y" } } } });
            Check("a playtest step with two actions is refused", twoInOne.IsError && Text(twoInOne).Contains("2 actions"), Text(twoInOne));
            var typoStep = await Call(reg, "playtest", new { steps = new object[] { new { wait = 500 } } });
            Check("a playtest step with an unknown key is refused", typoStep.IsError && Text(typoStep).Contains("unknown key") && Text(typoStep).Contains("wait"), Text(typoStep));
            var manyCaptures = await Call(reg, "playtest", new { steps = Enumerable.Repeat<object>(new { capture = true }, 9).ToArray() });
            Check("a playtest with more captures than the cap is refused", manyCaptures.IsError && Text(manyCaptures).Contains("capture steps"), Text(manyCaptures));

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

            // A probe that is not a string is that probe's error, not an exception that ends the
            // playtest with the game still running.
            var ptBad = await Call(reg, "playtest", new { steps = new object[] { new { sample = new { y = 5 } } } });
            Check("a playtest sample that is not an expression fails that probe, not the run",
                Text(ptBad).Contains("playtest") && Text(ptBad).Contains("given as a string"), Text(ptBad));

            // Abandoned halfway, a playtest still leaves play: the next call must not find the game
            // running (and, at OS level, a key held) because the caller stopped waiting.
            using (var abandon = new CancellationTokenSource(TimeSpan.FromMilliseconds(400)))
            {
                try
                {
                    await reg.CallTool(JsonSerializer.SerializeToElement(new
                    {
                        name = "playtest",
                        arguments = new { steps = new object[] { new { wait_ms = 5000 } } },
                    }), abandon.Token);
                }
                catch (OperationCanceledException) { }
            }
            var afterAbandon = await Call(reg, "get_play_state", new { });
            Check("a cancelled playtest still exits play",
                Text(afterAbandon).Contains("\"isPlaying\":false"), Text(afterAbandon));

            // A play mode that never starts is an error, and a playtest stops on it. Both used to say "completed": the
            // playtest then ran its assertions against the edit-mode scene and could answer PASS.
            fake.Play.WontStart = true;
            reg.PlaySettleTimeout = TimeSpan.FromSeconds(1);
            var noStart = await Call(reg, "enter_play", new { });
            Check("enter_play that never reaches play mode is an error", noStart.IsError && Text(noStart).Contains("isPlaying=false"), Text(noStart));
            var ptNoStart = await Call(reg, "playtest", new { criteria = new[] { new { expression = "z", op = "truthy" } } });
            Check("a playtest whose game never starts fails instead of passing", ptNoStart.IsError && !Text(ptNoStart).Contains("PASS"), Text(ptNoStart));
            fake.Play.WontStart = false;
            fake.Play.Unsupported = true;
            var noPlayMode = await Call(reg, "enter_play", new { });
            Check("enter_play on an engine without a play mode is an error that says so",
                noPlayMode.IsError && Text(noPlayMode).Contains("cannot enter play mode"), Text(noPlayMode));
            fake.Play.Unsupported = false;
            reg.PlaySettleTimeout = TimeSpan.FromSeconds(8);

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

            // The pin drops the live connection and reconnects once; when that attempt races the background retry loop the
            // answer is "the connection is still being made. Call engine_status again in a moment" — documented, and the
            // check used to fail on it about one run in four. It now does what the answer asks: waits, then reads again.
            var pinned = Text(await Call(reg, "engine_status", new { engine = "zebra" }));
            await engine.WaitForConnection(10_000, cts.Token);
            var switched = Text(await Call(reg, "engine_status", new { }));
            Check("pinning switches the live target", ConnectedName(switched) == "Zebra", pinned + " -> " + switched);

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

            // Pinning while disconnected connects in the same call: the answer used to be "call again in a moment"
            // while the retry loop waited out its back-off, up to ten seconds.
            var direct = Text(await Call(reg, "engine_status", new { engine = "loopback" }));
            Check("a pin made while disconnected connects in the same call", ConnectedName(direct) == "Loopback", direct);

            // Two editors of the same engine differ only by pid and project: a name pin cannot choose between them.
            var byProject = Text(await Call(reg, "engine_status", new { engine = "zebragame" }));
            Check("a pin by project name picks that editor", ConnectedName(byProject) == "Zebra", byProject);
            Check("engine_status reports the project of each editor",
                byProject.Contains("\"project\":\"ZebraGame\"") && byProject.Contains("\"project\":\"LoopbackGame\""), byProject);
            var pid = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var byPid = Text(await Call(reg, "engine_status", new { engine = pid }));
            Check("a pin by pid matches the editor running under it", engine.IsConnected && byPid.Contains($"\"pinned\":\"{pid}\""), byPid);

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
