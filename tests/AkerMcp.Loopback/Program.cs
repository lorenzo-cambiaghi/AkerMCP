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

            fake.Consent.Granted = true;
            await Task.Delay(5200);
            var lwAgain = await Call(reg, "list_windows", new { });
            Check("granting it again lets the tools through",
                !Text(lwAgain).Contains("refused"), Text(lwAgain));

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

            Check("an old spec still renders the length it asks for",
                Render("{\"wave\":\"saw\",\"freq\":1200,\"freq_sweep\":-3000,\"duration\":0.25}").Length == 11025);

            Check("an engine with no IAgentConsent is recognised, however it says so",
                ConsentGate.IsNoPolicyAnswer("NOT_SUPPORTED: whatever")
                && ConsentGate.IsNoPolicyAnswer("Engine does not implement IAgentConsent; ...")
                && ConsentGate.IsNoPolicyAnswer("InvalidOperationException: Unknown method: get_consent"));
            Check("a transient failure is not mistaken for an absent policy",
                !ConsentGate.IsNoPolicyAnswer("[ENGINE_DISCONNECTED] the pipe dropped")
                && !ConsentGate.IsNoPolicyAnswer("TimeoutException: the engine did not answer")
                && !ConsentGate.IsNoPolicyAnswer("NullReferenceException in the adapter"));

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
