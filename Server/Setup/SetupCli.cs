using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using AkerMcp.Shared.Ipc;
using AkerMcp.Shared.Setup;

namespace AkerMcp.Server.Setup
{
    /// <summary>
    /// The commands a person types (`setup`, `doctor`, `--version`, `--help`), hosted in the same binary as the stdio
    /// MCP server. They print to the console and exit before stdio JSON-RPC starts. Plain ASCII only: the Windows
    /// console's code page turns an em dash into garbage.
    /// </summary>
    internal static class SetupCli
    {
        private static readonly string[] Launchers = { "installed", "npx", "dotnet-tool", "source" };

        public static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args.Length == 0)
                return false;

            var cmd = args[0];
            if (cmd is "--version" or "-v" or "version")
            {
                Console.WriteLine($"AkerMcp.Server {ServerVersion.Product} (IPC protocol {IpcConstants.ProtocolVersion})");
                return true;
            }

            if (cmd is "--help" or "-h" or "help")
            {
                PrintHelp();
                return true;
            }

            if (string.Equals(cmd, "setup", StringComparison.OrdinalIgnoreCase))
            {
                exitCode = RunSetup(args.Skip(1).ToArray());
                return true;
            }

            if (string.Equals(cmd, "doctor", StringComparison.OrdinalIgnoreCase))
            {
                exitCode = RunDoctor(args.Skip(1).ToArray());
                return true;
            }

            return false;
        }

        private static void PrintHelp()
        {
            Console.WriteLine($"AkerMCP {ServerVersion.Product} - live MCP server for Unity, Godot 4 and Stride");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  aker-mcp [--profile <core|standard|full>]   Run the MCP server on stdio (what an AI client starts)");
            Console.WriteLine("  aker-mcp setup [options]                    Connect the AI clients on this machine, no JSON editing");
            Console.WriteLine("  aker-mcp doctor                             Check client configs, running editors and versions");
            Console.WriteLine("  aker-mcp --version");
            Console.WriteLine();
            Console.WriteLine("Setup options:");
            Console.WriteLine("  --client <id|all>        One client (" + string.Join(", ", AiClientCatalog.All.Select(c => c.Id)) + ")");
            Console.WriteLine("                           or all of them. Default: the ones installed on this machine.");
            Console.WriteLine("  --profile <name>         Tool profile the client starts the server with (default: standard).");
            Console.WriteLine("  --launcher <how>         What the client config runs:");
            Console.WriteLine("                             installed    this server, copied to " + ServerInstall.Root);
            Console.WriteLine("                             npx          npx -y aker-mcp@latest (needs Node 18+, follows new releases)");
            Console.WriteLine("                             dotnet-tool  aker-mcp from 'dotnet tool install -g AkerMcp.Server'");
            Console.WriteLine("                             source       dotnet run --project <this checkout's Server>");
            Console.WriteLine("                           Default: how this server was started.");
            Console.WriteLine("  --uninstall              Remove the 'game-engine' entry instead.");
            Console.WriteLine("  --dry-run                Show what would change; write nothing.");
        }

        private static int RunSetup(string[] args)
        {
            string? clientFilter = null;
            string? profile = null;
            string? launcher = null;
            bool uninstall = false;
            bool dryRun = false;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg is "--help" or "-h")
                {
                    PrintHelp();
                    return 0;
                }
                if (arg == "--uninstall") uninstall = true;
                else if (arg == "--dry-run") dryRun = true;
                else if (TryValue(args, ref i, "--client", out var c)) { if (c == null) return 2; clientFilter = c; }
                else if (TryValue(args, ref i, "--profile", out var p)) { if (p == null) return 2; profile = p; }
                else if (TryValue(args, ref i, "--launcher", out var l)) { if (l == null) return 2; launcher = l.ToLowerInvariant(); }
                else
                {
                    Console.Error.WriteLine($"Unknown setup option '{arg}'. Run 'aker-mcp --help' for usage.");
                    return 2;
                }
            }

            if (profile != null)
            {
                try { profile = ToolProfiles.Resolve(profile); }
                catch (ArgumentException ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    return 2;
                }
            }

            if (launcher != null && !Launchers.Contains(launcher))
            {
                Console.Error.WriteLine($"Unknown launcher '{launcher}'. Valid: {string.Join(", ", Launchers)}.");
                return 2;
            }

            IReadOnlyList<AiClientDefinition> targets;
            if (string.IsNullOrWhiteSpace(clientFilter))
            {
                targets = AiClientCatalog.All.Where(c => c.IsInstalled).ToList();
                if (targets.Count == 0)
                {
                    Console.WriteLine("No AI client found on this machine. Name one explicitly:");
                    foreach (var c in AiClientCatalog.All)
                        Console.WriteLine($"  aker-mcp setup --client {c.Id,-15} ({c.DisplayName})");
                    return 0;
                }
            }
            else if (string.Equals(clientFilter, "all", StringComparison.OrdinalIgnoreCase))
            {
                targets = AiClientCatalog.All;
            }
            else
            {
                var matched = AiClientCatalog.FindById(clientFilter!);
                if (matched == null)
                {
                    var valid = string.Join(", ", AiClientCatalog.All.Select(c => c.Id));
                    Console.Error.WriteLine($"Unknown client '{clientFilter}'. Valid clients: {valid}, all");
                    return 2;
                }
                targets = new[] { matched };
            }

            McpServerLaunchSpec? spec = null;
            if (!uninstall)
            {
                try
                {
                    spec = ResolveLaunchSpec(launcher, profile, dryRun);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is PlatformNotSupportedException)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    return 1;
                }
            }

            Console.WriteLine(uninstall
                ? "Removing AkerMCP ('game-engine') from AI clients:"
                : $"Connecting AI clients to AkerMCP ('game-engine' -> {spec}):");
            Console.WriteLine();

            var failures = 0;
            foreach (var client in targets)
            {
                var result = uninstall
                    ? McpConfigPatcher.Remove(client, dryRun: dryRun)
                    : McpConfigPatcher.Configure(client, spec!, dryRun: dryRun);

                var mark = result.Success ? (result.Changed ? "[OK]" : "[--]") : "[FAIL]";
                Console.WriteLine($"  {mark} {client.DisplayName}: {result.Message}");
                if (result.BackupPath != null)
                    Console.WriteLine($"       backup: {result.BackupPath}");
                if (!result.Success)
                    failures++;

                if (!uninstall)
                {
                    var projects = McpConfigPatcher.Inspect(client).ProjectEntries;
                    if (projects.Count > 0)
                        Console.WriteLine($"       note: {projects.Count} project(s) have their own 'game-engine' entry, which wins inside them: " +
                                          string.Join(", ", projects.Take(3)) + (projects.Count > 3 ? ", ..." : ""));
                }
            }

            Console.WriteLine();
            if (failures == 0 && !uninstall && !dryRun)
                Console.WriteLine("Next: open your editor (Unity, Godot or Stride) with the AkerMcp plugin running, then 'aker-mcp doctor' to check.");

            return failures == 0 ? 0 : 1;
        }

        /// <summary>"--name value" or "--name=value". Out null (after printing why) when the value is missing.</summary>
        private static bool TryValue(string[] args, ref int i, string name, out string? value)
        {
            value = null;
            if (args[i] == name)
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine($"Error: {name} requires a value.");
                    return true;
                }
                value = args[++i];
                return true;
            }
            if (args[i].StartsWith(name + "=", StringComparison.Ordinal))
            {
                value = args[i].Substring(name.Length + 1);
                return true;
            }
            return false;
        }

        private static int RunDoctor(string[] args)
        {
            if (args.Length > 0)
            {
                if (args[0] is "--help" or "-h") { PrintHelp(); return 0; }
                Console.Error.WriteLine($"Unknown doctor option '{args[0]}'.");
                return 2;
            }

            Console.WriteLine($"AkerMCP doctor - server {ServerVersion.Product} (IPC protocol {IpcConstants.ProtocolVersion})");
            Console.WriteLine();

            Console.WriteLine("AI clients:");
            var ready = 0;
            var installed = 0;
            var problems = 0;
            foreach (var client in AiClientCatalog.All)
            {
                if (!client.IsInstalled) continue;
                installed++;
                var status = McpConfigPatcher.Inspect(client);
                var connection = AiConnection.Classify(status);

                string mark;
                switch (connection.Kind)
                {
                    case ConnectionKind.NotConfigured: mark = "[  ]"; break;
                    case ConnectionKind.Unreadable:
                    case ConnectionKind.Broken: mark = "[!!]"; problems++; break;
                    default: mark = "[OK]"; ready++; break;
                }

                var profile = connection.Kind == ConnectionKind.NotConfigured ? "" : $", profile {connection.Profile}";
                Console.WriteLine($"  {mark} {client.DisplayName,-20} {connection.Summary}{profile}");

                if (connection.Kind == ConnectionKind.Npx && FindOnPath("npx") == null)
                {
                    Console.WriteLine("       npx is not on PATH: install Node.js 18+, or run 'aker-mcp setup' from the release binary.");
                    problems++;
                }
                if (connection.Kind == ConnectionKind.Broken || connection.Kind == ConnectionKind.Unreadable)
                    Console.WriteLine($"       fix: aker-mcp setup --client {client.Id}" +
                                      (connection.Kind == ConnectionKind.Unreadable ? $" (after fixing {client.ConfigPath})" : ""));
                if (connection.Kind == ConnectionKind.Installed && connection.ServerVersion != ServerVersion.Product)
                    Console.WriteLine($"       this is server {connection.ServerVersion}; 'aker-mcp setup --client {client.Id}' switches it to {ServerVersion.Product}.");

                if (status.ProjectEntries.Count > 0)
                {
                    ready += connection.Kind == ConnectionKind.NotConfigured ? 1 : 0;
                    Console.WriteLine($"       {status.ProjectEntries.Count} project(s) have their own entry, which wins inside them: " +
                                      string.Join(", ", status.ProjectEntries.Take(3)) + (status.ProjectEntries.Count > 3 ? ", ..." : ""));
                }
            }
            if (installed == 0)
                Console.WriteLine("  (no supported AI client found in the usual folders)");

            Console.WriteLine();

            Console.WriteLine("Running editors:");
            var engines = EngineConnection.DiscoverEngines();
            if (engines.Count == 0)
            {
                Console.WriteLine("  [  ] None. Open Unity (Window > AkerMcp > Start), Godot (enable the AkerMcp plugin) or Stride.");
            }
            else
            {
                foreach (var e in engines)
                {
                    var project = string.IsNullOrEmpty(e.Project) ? "" : $", project '{e.Project}'";
                    var (breaking, skew) = EngineConnection.DescribeVersionSkew(e.ProtocolVersion, IpcConstants.ProtocolVersion);
                    var tag = breaking ? "[!!]" : "[OK]";
                    if (breaking) problems++;
                    Console.WriteLine($"  {tag} {e.Engine} {e.Version} (pid {e.Pid}{project}, protocol {e.ProtocolVersion})");
                    if (skew != null)
                        Console.WriteLine($"       note: {skew}");
                }
            }

            Console.WriteLine();
            if (problems > 0)
                Console.WriteLine("Fix the [!!] lines above first.");
            else if (ready == 0)
                Console.WriteLine("Next: 'aker-mcp setup' connects the AI clients found above.");
            else if (engines.Count == 0)
                Console.WriteLine("Your AI client is ready: start the AkerMcp plugin in your editor.");
            else
                Console.WriteLine("Everything is ready. Ask your AI: \"Inspect the scene hierarchy\".");

            return problems > 0 ? 1 : 0;
        }

        /// <summary>
        /// What the client config should run. Without --launcher: npx when the npm launcher started this process (it
        /// says so in AKER_MCP_LAUNCHER), the global tool when that is what runs, the source checkout under `dotnet run`,
        /// and otherwise this very binary, copied to the per-user install folder first.
        /// </summary>
        internal static McpServerLaunchSpec ResolveLaunchSpec(string? launcher, string? profile, bool dryRun)
        {
            var procPath = Environment.ProcessPath;
            var procName = string.IsNullOrEmpty(procPath) ? "" : Path.GetFileNameWithoutExtension(procPath).ToLowerInvariant();
            // A build output (bin/Release/net8.0) has a small apphost next to AkerMcp.Server.dll: copied alone it starts nothing.
            var isBuildOutput = !string.IsNullOrEmpty(procPath) &&
                                File.Exists(Path.Combine(Path.GetDirectoryName(procPath)!, "AkerMcp.Server.dll"));

            if (launcher == null)
            {
                if (string.Equals(Environment.GetEnvironmentVariable("AKER_MCP_LAUNCHER"), "npx", StringComparison.OrdinalIgnoreCase))
                    launcher = "npx";
                else if (procName == "aker-mcp")
                    launcher = "dotnet-tool";
                else if (procName == "dotnet" || isBuildOutput)
                    launcher = "source";
                else
                    launcher = "installed";
            }

            switch (launcher)
            {
                case "npx":
                    return McpServerLaunchSpec.NpxLatest(profile);
                case "dotnet-tool":
                    return McpServerLaunchSpec.DotnetTool(profile);
                case "source":
                    var project = FindSourceProject()
                        ?? throw new InvalidOperationException("--launcher source needs a server built from a checkout of the repository.");
                    var configuration = AppContext.BaseDirectory.Replace('\\', '/').Contains("/bin/Debug/") ? "Debug" : "Release";
                    return McpServerLaunchSpec.FromSource(project, configuration, profile);
                default:
                    if (string.IsNullOrEmpty(procPath) || procName == "dotnet" || isBuildOutput)
                        throw new InvalidOperationException("--launcher installed needs the standalone AkerMcp.Server binary (from the release zip).");
                    var target = ServerInstall.ExecutablePath(ServerVersion.Product);
                    if (!dryRun)
                    {
                        var placed = ServerInstall.InstallCopyOf(procPath!, ServerVersion.Product);
                        if (!string.Equals(Path.GetFullPath(placed), Path.GetFullPath(procPath!), StringComparison.OrdinalIgnoreCase))
                            Console.WriteLine($"Installed the server at {placed}");
                        target = placed;
                    }
                    return McpServerLaunchSpec.Executable(target, profile);
            }
        }

        /// <summary>The Server project directory of the checkout this build came from (bin/&lt;Configuration&gt;/net8.0/ under it).</summary>
        private static string? FindSourceProject()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 5 && dir != null; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AkerMcp.Server.csproj")))
                    return dir.FullName.Replace('\\', '/');
            }
            return null;
        }

        private static string? FindOnPath(string name)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            var exts = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';')
                : new[] { "" };
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                foreach (var ext in exts)
                {
                    try
                    {
                        var candidate = Path.Combine(dir.Trim('"'), name + ext.ToLowerInvariant());
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch (ArgumentException) { /* a malformed PATH entry */ }
                }
            }
            return null;
        }
    }
}
