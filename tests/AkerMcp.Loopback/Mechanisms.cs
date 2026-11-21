using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AkerMcp.Server;
using AkerMcp.Shared.Protocol;
using AkerMcp.Shared.Reflection;
using AkerMcp.Shared.Serialization;
using AkerMcp.Shared.Setup;

namespace AkerMcp.Loopback
{
    /// <summary>
    /// Contracts of the value path — what a value that does not fit becomes, which member a read goes through, how long a
    /// forwarded call waits — checked on the shared code itself. The engine-specific halves (a Unity renderer, a Game view)
    /// cannot run here; the rules they delegate to can.
    /// </summary>
    internal static class Mechanisms
    {
        public static async Task Run(Func<string, object, Task<ToolResult>> call, Action<string, bool, string?> check)
        {
            MediaMechanisms.Run(check);
            await ValueMechanisms.Run(check);
            Serializer(check);
            Resolver(check);
            SharedTwins(check);
            Deadlines(check);
            ConfigPatching(check);
            await ErrorsReachTheCaller(call, check);
        }

        // ---- GenericSerializer: a value that does not fit is an error, never a zero ----
        private static void Serializer(Action<string, bool, string?> check)
        {
            var registry = new TypeRegistry();
            // Shaped like Unity's registrations: a colour whose alpha defaults to 1, a box of two nested vectors, a mask of one member.
            registry.RegisterCustomSerializer<TestColor>(
                c => new Dictionary<string, object?> { ["r"] = c.R, ["g"] = c.G, ["b"] = c.B, ["a"] = c.A },
                d => new TestColor { R = F(d, "r"), G = F(d, "g"), B = F(d, "b"), A = F(d, "a", 1f) });
            registry.RegisterCustomSerializer<TestBox>(
                b => new Dictionary<string, object?> { ["center"] = Vec(b.Center), ["size"] = Vec(b.Size) },
                d => new TestBox { Center = Vec(d, "center"), Size = Vec(d, "size") });
            registry.RegisterCustomSerializer<TestMask>(
                m => new Dictionary<string, object?> { ["value"] = m.Value },
                d => new TestMask { Value = (int)F(d, "value") });
            var serializer = new GenericSerializer(registry);
            // A read that throws answers its message instead: a broken mechanism must turn a check red, not end the run.
            object? Read(string json, Type type)
            {
                try { return serializer.JsonElementToObject(Parse(json), type); }
                catch (Exception ex) { return ex.Message; }
            }

            var array = Error(Read("[1,2,3]", typeof(FakeVector)));
            check("a struct refuses a JSON array instead of reading it as zero", array.Contains("JSON object"), array);
            var extra = Error(Read("{\"x\":1,\"y\":2,\"z\":3,\"w\":4}", typeof(FakeVector)));
            check("a struct refuses a member it does not have", extra.Contains("no member w"), extra);
            var partial = Error(Read("{\"y\":5}", typeof(FakeVector)));
            check("a struct refuses a JSON object that leaves members out", partial.Contains("missing x, z"), partial);
            var whole = Read("{\"X\":1,\"y\":2,\"z\":3}", typeof(FakeVector));
            check("a struct given every member still reads, in any case",
                whole is FakeVector v && v.x == 1 && v.y == 2 && v.z == 3, whole?.ToString());

            var typo = Error(Read("\"Sollid\"", typeof(FakeMode)));
            check("an enum refuses a name it does not have, naming the ones it has", typo.Contains("SolidColor"), typo);
            var undefined = Error(Read("7", typeof(FakeMode)));
            check("an enum refuses a number it does not define", undefined.Contains("not a FakeMode"), undefined);
            check("a [Flags] enum takes a combination of its bits",
                Equals(Read("3", typeof(TestFlags)), TestFlags.A | TestFlags.B)
                && Equals(Read("\"a, b\"", typeof(TestFlags)), TestFlags.A | TestFlags.B), null);

            var opaque = Read("{\"r\":1,\"g\":0,\"b\":0}", typeof(TestColor));
            check("a registered struct may leave out a member its deserializer defaults (alpha 1)",
                opaque is TestColor c && c.R == 1 && c.A == 1, opaque?.ToString());
            var red = Error(Read("{\"r\":1}", typeof(TestColor)));
            check("a registered struct refuses a missing member that would read as zero",
                red.EndsWith("missing g, b"), red);
            var notAColour = Error(Read("\"#FF0000\"", typeof(TestColor)));
            check("a registered struct refuses text instead of reading it as black", notAColour.Contains("JSON object"), notAColour);

            var box = new TestBox { Center = new FakeVector(1, 2, 3), Size = new FakeVector(4, 5, 6) };
            var roundTrip = Read(serializer.ObjectToJsonElement(box).GetRawText(), typeof(TestBox));
            check("a registered struct of nested objects reads back what it wrote (Bounds, Aabb)",
                roundTrip is TestBox back && back.Center.y == 2 && back.Size.z == 6, roundTrip?.ToString());
            var nestedPartial = Error(Read("{\"center\":{\"x\":1},\"size\":{\"x\":1,\"y\":1,\"z\":1}}", typeof(TestBox)));
            check("a nested member left out is named with its path", nestedPartial.Contains("center.y"), nestedPartial);
            var mask = Read("256", typeof(TestMask));
            check("a struct of one member takes the bare value (a LayerMask as 256)", mask is TestMask m && m.Value == 256, mask?.ToString());

            var id = Guid.NewGuid();
            var idJson = serializer.ObjectToJsonElement(id);
            check("a Guid is written as its text, not taken apart as a struct",
                idJson.ValueKind == JsonValueKind.String && idJson.GetString() == id.ToString()
                && Equals(Read(idJson.GetRawText(), typeof(Guid)), id), idJson.GetRawText());
        }

        /// <summary>What a refused read answered, or "(no error)" when it produced a value.</summary>
        private static string Error(object? read) => read as string ?? "(no error)";

        // ---- PropertyPathResolver: the reason reaches the message, and the member keeps its value ----
        private static void Resolver(Action<string, bool, string?> check)
        {
            var state = new FakeProbeState { Mode = FakeMode.SolidColor, Position = new FakeVector(1, 2, 3) };
            var resolver = new PropertyPathResolver();

            var typo = Fails(() => resolver.Set(state, "Mode", "Sollid"), out var typoError);
            check("set through the resolver refuses an enum typo as a value error, with the names",
                typoError is PropertyValueException && typo.Contains("SolidColor") && state.Mode == FakeMode.SolidColor, typo);
            var array = Fails(() => resolver.Set(state, "Position", "[9,9,9]"), out var arrayError);
            check("set through the resolver refuses a vector given as an array and leaves it as it was",
                arrayError is PropertyValueException && state.Position.x == 1 && state.Position.z == 3, array);
        }

        // ---- The shared twin: which member a read goes through when the getter would make a copy ----
        private static void SharedTwins(Action<string, bool, string?> check)
        {
            check("a member with a shared twin reads through it (material, materials, fontMaterial)",
                SharedMembers.SharedTwin(typeof(TestRenderer), "material") == "sharedMaterial"
                && SharedMembers.SharedTwin(typeof(TestRenderer), "materials") == "sharedMaterials"
                && SharedMembers.SharedTwin(typeof(TestText), "fontMaterial") == "fontSharedMaterial", null);
            check("a member without a twin, or the twin itself, reads as it is",
                SharedMembers.SharedTwin(typeof(TestRenderer), "enabled") == null
                && SharedMembers.SharedTwin(typeof(TestRenderer), "sharedMaterial") == null, null);
            var nested = SharedMembers.ToSharedPath(typeof(TestRenderer), "material.color");
            var indexed = SharedMembers.ToSharedPath(typeof(TestRenderer), "materials[0].color");
            check("a path is rewritten member by member, index kept",
                nested == "sharedMaterial.color" && indexed == "sharedMaterials[0].color", nested + " / " + indexed);
            var unresolved = SharedMembers.ToSharedPath(typeof(TestRenderer), "nosuch.material");
            check("a path that stops resolving is left to the resolver to report", unresolved == "nosuch.material", unresolved);
        }

        // ---- EngineConnection: the deadline follows timeout_ms all the way up ----
        private static void Deadlines(Action<string, bool, string?> check)
        {
            using var connection = new EngineConnection();
            var huge = connection.RequestTimeoutFor(Parse("{\"timeout_ms\":" + int.MaxValue + "}"));
            check("a huge timeout_ms does not wrap the deadline back to the default", huge == int.MaxValue, huge.ToString());
            var minute = connection.RequestTimeoutFor(Parse("{\"timeout_ms\":60000}"));
            check("timeout_ms gets the reply margin on top", minute == 65_000, minute.ToString());
            var skew = EngineConnection.DescribeVersionSkew("1.10.0", "1.7.0");
            check("version skew compares by number (1.10 is newer than 1.7)",
                !skew.breaking && skew.message != null && skew.message.Contains("after the server's version"), skew.message);
        }

        // ---- Setup / McpConfigPatcher: safe merge, backup, idempotence, dry-run and clean removal ----
        private static void ConfigPatching(Action<string, bool, string?> check)
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), "akermcp-setup-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            try
            {
                var cfgPath = Path.Combine(tmpDir, "client_config.json");
                var client = new AiClientDefinition("test-client", "Test Client", cfgPath, tmpDir, "mcpServers", includeTypeStdio: true);

                // Dry-run does not create the file
                var spec = McpServerLaunchSpec.NpxLatest("full", windows: false);
                var dry = McpConfigPatcher.Configure(client, spec, dryRun: true);
                check("setup dry-run reports change without touching disk", dry.Success && dry.Changed && !File.Exists(cfgPath), dry.Message);

                // A client that starts its server without a shell gets ENOENT for a bare "npx" on Windows (npx.cmd).
                var onWindows = McpServerLaunchSpec.NpxLatest("full", windows: true);
                check("npx goes through cmd /c on Windows and runs bare elsewhere",
                    onWindows.Command == "cmd" && onWindows.Args.Count >= 4 && onWindows.Args[0] == "/c" && onWindows.Args[1] == "npx" &&
                    onWindows.Args[3] == "aker-mcp@latest" && spec.Command == "npx" && spec.Args[0] == "-y",
                    $"{onWindows} | {spec}");

                // Existing config with another server, comments, and trailing commas
                File.WriteAllText(cfgPath, "{\n  // user comment\n  \"theme\": \"dark\",\n  \"mcpServers\": {\n    \"other-tool\": { \"command\": \"other\", \"args\": [], },\n  },\n}\n");

                var first = McpConfigPatcher.Configure(client, spec);
                check("setup merges game-engine and creates a backup",
                    first.Success && first.Changed && first.BackupPath != null && File.Exists(first.BackupPath), first.Message);
                check("a file whose comments cannot be kept says so and names the backup",
                    first.Message.Contains("comments") && first.BackupPath != null && first.Message.Contains(first.BackupPath)
                    && File.ReadAllText(first.BackupPath).Contains("// user comment"),
                    first.Message);

                var status = McpConfigPatcher.Inspect(client);
                check("inspect reads back the written command and profile args",
                    status.IsConfigured && status.Command == "npx" && status.Args.Count == 4 && status.Args[3] == "full",
                    $"{status.Command} [{string.Join(",", status.Args)}]");

                var writtenJson = File.ReadAllText(cfgPath);
                check("setup preserves unrelated settings and other MCP servers",
                    writtenJson.Contains("\"theme\": \"dark\"") && writtenJson.Contains("\"other-tool\"") && writtenJson.Contains("\"type\": \"stdio\""),
                    writtenJson);

                // Hand-edited files keep their characters: the default encoder would turn every "é" and "<" in the file into \u escapes.
                var accentsPath = Path.Combine(tmpDir, "accents.json");
                File.WriteAllText(accentsPath, "{\n  \"note\": \"José's <scene> & co\",\n  \"mcpServers\": { \"game-engine\": { \"command\": \"x\", \"args\": [], \"env\": { \"AKER_MCP_PROFILE\": \"full\" } } }\n}\n");
                var accentsClient = new AiClientDefinition("accents", "Accents", accentsPath, tmpDir);
                var reapplied = McpConfigPatcher.Configure(accentsClient, McpServerLaunchSpec.Executable("/opt/aker/AkerMcp.Server"));
                var accentsText = File.ReadAllText(accentsPath);
                check("rewriting a config leaves non-ASCII and HTML characters as they were",
                    reapplied.Success && accentsText.Contains("José's <scene> & co"), accentsText);
                check("re-applying the entry keeps keys AkerMCP does not manage (env)",
                    accentsText.Contains("\"AKER_MCP_PROFILE\": \"full\"") && accentsText.Contains("/opt/aker/AkerMcp.Server"), accentsText);

                // Claude Code keeps per-project servers under "projects"; inside those projects they win over the user-level one.
                var claudePath = Path.Combine(tmpDir, "claude.json");
                File.WriteAllText(claudePath, "{ \"projects\": { \"C:/game\": { \"mcpServers\": { \"game-engine\": { \"command\": \"dotnet\" } } }, \"C:/other\": { \"mcpServers\": {} } } }");
                var claudeClient = new AiClientDefinition("cc", "CC", claudePath, tmpDir, includeTypeStdio: true, projectsProperty: "projects");
                var claudeStatus = McpConfigPatcher.Inspect(claudeClient);
                check("per-project entries (Claude Code local scope) are found, user-level stays unconfigured",
                    !claudeStatus.IsConfigured && claudeStatus.ProjectEntries.Count == 1 && claudeStatus.ProjectEntries[0] == "C:/game",
                    string.Join(",", claudeStatus.ProjectEntries));

                var second = McpConfigPatcher.Configure(client, spec);
                check("setup is idempotent when already configured identically", second.Success && !second.Changed, second.Message);

                var removed = McpConfigPatcher.Remove(client);
                var afterRemove = McpConfigPatcher.Inspect(client);
                check("uninstall removes game-engine while keeping other servers intact",
                    removed.Success && removed.Changed && !afterRemove.IsConfigured && File.ReadAllText(cfgPath).Contains("\"other-tool\""),
                    removed.Message);

                ServerInstallLayout(check, tmpDir);
                ConnectionKinds(check, tmpDir);
                TomlPatching(check, tmpDir);
            }
            finally
            {
                try { Directory.Delete(tmpDir, recursive: true); } catch { /* ignore */ }
            }
        }

        // ---- ServerInstall: the folder an AI client config names, and how a release zip lands in it ----
        private static void ServerInstallLayout(Action<string, bool, string?> check, string tmpDir)
        {
            var root = Path.Combine(tmpDir, "server");
            var exe = ServerInstall.ExecutablePath("1.2.3", root, "win-x64");
            check("an installed server's path gives back its version; anything else gives null",
                ServerInstall.VersionOf(exe, root) == "1.2.3"
                && ServerInstall.VersionOf(Path.Combine(root, "latest", "win-x64", "AkerMcp.Server.exe"), root) == null
                && ServerInstall.VersionOf(Path.Combine(root + "-v1.2.3", "win-x64", "AkerMcp.Server.exe"), root) == null // a sibling folder sharing the prefix
                && ServerInstall.VersionOf("npx", root) == null,
                exe);

            // The release zips hold their files under "./", as build-package's tar writes them.
            var zipPath = Path.Combine(tmpDir, "release.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                archive.CreateEntry("./");
                using (var w = new StreamWriter(archive.CreateEntry("./" + ServerInstall.ExecutableName).Open())) w.Write("binary");
                using (var w = new StreamWriter(archive.CreateEntry("./AkerMcp.Server.pdb").Open())) w.Write("symbols");
            }
            var placed = ServerInstall.InstallFromZip(zipPath, "v1.2.3", root, "win-x64");
            var again = ServerInstall.InstallFromZip(zipPath, "1.2.3", root, "win-x64");
            var leftovers = Directory.GetDirectories(root, ".staging-*");
            check("a release zip unpacks into <root>/v<version>/<rid>/, a second install finds it in place, no staging left",
                placed == again && File.Exists(placed) && File.ReadAllText(placed) == "binary"
                && Path.GetFileName(Path.GetDirectoryName(placed)) == "win-x64" && leftovers.Length == 0,
                $"{placed} | {leftovers.Length} staging");
        }

        // ---- Codex's config.toml: one table edited, every other byte left alone ----
        private static void TomlPatching(Action<string, bool, string?> check, string tmpDir)
        {
            // Shaped like a real ~/.codex/config.toml on Windows: CRLF, literal-string paths, another server with an env subtable.
            const string original =
                "# my settings\r\nmodel = \"gpt-5\"\r\n\r\n" +
                "[mcp_servers.node_repl]\r\ncommand = 'C:\\Program Files\\x\\node.exe'\r\nargs = []\r\n\r\n" +
                "[mcp_servers.node_repl.env]\r\nA = \"1\"\r\n\r\n" +
                "[projects.'C:\\work']\r\ntrust_level = \"trusted\"\r\n";
            var path = Path.Combine(tmpDir, "config.toml");
            File.WriteAllText(path, original);
            var codex = new AiClientDefinition("codex", "Codex", path, tmpDir, rootProperty: "mcp_servers", format: ConfigFormat.Toml);

            var exe = @"C:\Users\José\AkerMcp\server\v1.2.3\win-x64\AkerMcp.Server.exe";
            var added = McpConfigPatcher.Configure(codex, McpServerLaunchSpec.Executable(exe, "full"));
            var text = File.ReadAllText(path);
            var read = McpConfigPatcher.Inspect(codex);
            check("codex: the table is appended, the rest of the file is untouched byte for byte, CRLF kept",
                added.Success && added.Changed && text.StartsWith(original, StringComparison.Ordinal)
                && text.Contains("\r\n[mcp_servers.game-engine]\r\n") && !text.Replace("\r\n", "").Contains("\n"),
                text);
            check("codex: inspect reads back the Windows path and the arguments",
                read.IsConfigured && read.Command == exe && read.Args.Count == 2 && read.Args[1] == "full",
                $"{read.Command} [{string.Join(",", read.Args)}] {read.ParseError}");

            var again = McpConfigPatcher.Configure(codex, McpServerLaunchSpec.Executable(exe, "full"));
            var removed = McpConfigPatcher.Remove(codex);
            check("codex: applying twice changes nothing; removing gives back the original file exactly",
                again.Success && !again.Changed && removed.Success && removed.Changed && File.ReadAllText(path) == original,
                File.ReadAllText(path));

            // A table the user edited: multi-line args with a comment, keys of their own, an env subtable, a table after it.
            File.WriteAllText(path,
                "[mcp_servers.\"game-engine\"]\ncommand = \"old\"\nargs = [\n  \"--profile\", # theirs\n  \"core\",\n]\n" +
                "startup_timeout_sec = 120\nenv_vars = [\"X\"]\n\n[mcp_servers.game-engine.env]\nAKER = \"1\"\n\n[other]\nk = 1\n");
            var reapplied = McpConfigPatcher.Configure(codex, McpServerLaunchSpec.Executable(exe));
            var edited = File.ReadAllText(path);
            var afterEdit = McpConfigPatcher.Inspect(codex);
            check("codex: re-applying replaces command and args only; the user's keys, subtable and the next table stay",
                reapplied.Success && afterEdit.Command == exe && afterEdit.Args.Count == 0
                && edited.Contains("startup_timeout_sec = 120") && edited.Contains("env_vars = [\"X\"]")
                && edited.Contains("[mcp_servers.game-engine.env]\nAKER = \"1\"") && edited.Contains("[other]\nk = 1")
                && !edited.Contains("old") && !edited.Contains("theirs"),
                edited);
            McpConfigPatcher.Remove(codex);
            var withoutSub = File.ReadAllText(path);
            check("codex: removing the server removes its subtables too, and only them",
                !withoutSub.Contains("game-engine") && !withoutSub.Contains("AKER") && withoutSub == "[other]\nk = 1\n",
                withoutSub);

            // A path with quotes cannot be a literal string: it goes out as a basic string with escapes and comes back the same.
            var odd = @"C:\it's ""odd""\AkerMcp.Server.exe";
            McpConfigPatcher.Configure(codex, McpServerLaunchSpec.Executable(odd));
            check("codex: a value with quotes and backslashes is written as a valid escaped string and reads back the same",
                McpConfigPatcher.Inspect(codex).Command == odd
                && File.ReadAllText(path).Contains(@"command = ""C:\\it's \""odd\""\\AkerMcp.Server.exe"""),
                File.ReadAllText(path));

            // Defined as an inline table: not a form this edits. It must refuse and leave the file alone.
            const string inline = "[mcp_servers]\ngame-engine = { command = \"x\", args = [] }\n";
            File.WriteAllText(path, inline);
            var refused = McpConfigPatcher.Configure(codex, McpServerLaunchSpec.Executable(exe));
            var inlineStatus = AiConnection.Classify(McpConfigPatcher.Inspect(codex));
            check("codex: a server defined as an inline table is refused, the file untouched, the entry reported unreadable",
                !refused.Success && File.ReadAllText(path) == inline && inlineStatus.Kind == ConnectionKind.Unreadable,
                refused.Message);
        }

        // ---- AiConnection: what a client entry starts, as doctor and the editor windows report it ----
        private static void ConnectionKinds(Action<string, bool, string?> check, string tmpDir)
        {
            var root = Path.Combine(tmpDir, "server");
            var installed = ServerInstall.ExecutablePath("1.2.3", root, "win-x64"); // created by ServerInstallLayout
            AiConnection Of(string command, bool windows, params string[] args) =>
                AiConnection.Classify(new ClientConfigStatus { FileExists = true, IsConfigured = true, Command = command, Args = args }, root, windows);

            var current = Of(installed, true, "--profile", "full");
            var gone = Of(ServerInstall.ExecutablePath("9.9.9", root, "win-x64"), true);
            check("an entry on the installed server reports its version and profile; one whose file is gone is broken",
                current.Kind == ConnectionKind.Installed && current.ServerVersion == "1.2.3" && current.RunsVersion("v1.2.3")
                && !current.RunsVersion("1.2.4") && current.Profile == "full" && gone.Kind == ConnectionKind.Broken,
                $"{current.Kind} {current.ServerVersion} {current.Profile} | {gone.Kind}");

            var bareNpxWindows = Of("npx", true, "-y", "aker-mcp@latest");
            var cmdNpx = Of("cmd", true, "/c", "npx", "-y", "aker-mcp@latest");
            var bareNpxMac = Of("npx", false, "-y", "aker-mcp@latest");
            check("a bare npx is broken on Windows only; cmd /c npx is the npm launcher",
                bareNpxWindows.Kind == ConnectionKind.Broken && cmdNpx.Kind == ConnectionKind.Npx && bareNpxMac.Kind == ConnectionKind.Npx,
                $"{bareNpxWindows.Kind} {cmdNpx.Kind} {bareNpxMac.Kind}");

            var source = Of("dotnet", true, "run", "--project", "C:/src/Server");
            var tool = Of("aker-mcp", true);
            var custom = Of(Path.Combine(tmpDir, "accents.json"), true); // any file that exists
            var unconfigured = AiConnection.Classify(new ClientConfigStatus { FileExists = true }, root, true);
            check("source, global tool, a command of the user's own and no entry are told apart",
                source.Kind == ConnectionKind.Source && tool.Kind == ConnectionKind.DotnetTool && custom.Kind == ConnectionKind.Custom
                && unconfigured.Kind == ConnectionKind.NotConfigured && !custom.IsManaged && cmdNpx.IsManaged,
                $"{source.Kind} {tool.Kind} {custom.Kind} {unconfigured.Kind}");
        }

        // ---- Through the pipe: a refused value and an unknown level come back as errors ----
        private static async Task ErrorsReachTheCaller(Func<string, object, Task<ToolResult>> call, Action<string, bool, string?> check)
        {
            var before = FakeProbeNode.State.Position;
            var set = await call("set_property", new { object_path = FakeProbeNode.ProbePath, property_path = "Position", value = new[] { 9, 9, 9 } });
            var after = FakeProbeNode.State.Position;
            check("set_property answers an error for a vector given as an array, and leaves it as it was",
                set.IsError && Text(set).Contains("JSON object") && after.x == before.x && after.y == before.y && after.z == before.z, Text(set));

            var plural = await call("get_console_logs", new { level_filter = "warnings", count = 5 });
            check("get_console_logs reads a plural level as the level", !plural.IsError && Text(plural).Contains("loopback warning marker"), Text(plural));
            var unknown = await call("get_console_logs", new { level_filter = "loud" });
            check("get_console_logs refuses an unknown level, naming the ones there are",
                unknown.IsError && Text(unknown).Contains("error, warning, info, debug or all"), Text(unknown));
        }

        private static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private static string Text(ToolResult r) => r.Content.Count > 0 ? (r.Content[0].Text ?? "") : "";

        private static string Fails(Action action) => Fails(action, out _);

        /// <summary>The exception's message, or "(no error)" when the action went through.</summary>
        private static string Fails(Action action, out Exception? error)
        {
            try { action(); error = null; return "(no error)"; }
            catch (Exception ex) { error = ex; return ex.Message; }
        }

        private static float F(Dictionary<string, object?> d, string key, float def = 0f) =>
            d.TryGetValue(key, out var v) && v != null ? Convert.ToSingle(v, System.Globalization.CultureInfo.InvariantCulture) : def;

        private static Dictionary<string, object?> Vec(FakeVector v) => new Dictionary<string, object?> { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };

        private static FakeVector Vec(Dictionary<string, object?> d, string key) =>
            d.TryGetValue(key, out var v) && v is Dictionary<string, object?> sub ? new FakeVector(F(sub, "x"), F(sub, "y"), F(sub, "z")) : default;
    }

    public struct TestColor { public float R, G, B, A; public override string ToString() => $"{R} {G} {B} {A}"; }
    public struct TestBox { public FakeVector Center, Size; public override string ToString() => $"{Center.x},{Center.y},{Center.z} / {Size.x},{Size.y},{Size.z}"; }
    public struct TestMask { public int Value; }

    [Flags]
    public enum TestFlags { None = 0, A = 1, B = 2 }

    /// <summary>Shaped like UnityEngine.Renderer: getters that copy, next to their shared twins.</summary>
    public sealed class TestRenderer
    {
        public TestMaterial material => new TestMaterial();
        public TestMaterial sharedMaterial => new TestMaterial();
        public TestMaterial[] materials => new TestMaterial[0];
        public TestMaterial[] sharedMaterials => new TestMaterial[0];
        public bool enabled => true;
    }

    /// <summary>Shaped like TextMeshPro's TMP_Text: the twin of fontMaterial is fontSharedMaterial.</summary>
    public sealed class TestText
    {
        public TestMaterial fontMaterial => new TestMaterial();
        public TestMaterial fontSharedMaterial => new TestMaterial();
    }

    public sealed class TestMaterial
    {
        public float color => 1f;
    }
}
