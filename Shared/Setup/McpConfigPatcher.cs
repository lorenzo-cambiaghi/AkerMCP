using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AkerMcp.Shared.Setup
{
    /// <summary>What an AI client runs to start the server: a command and its arguments, as written in the client config.</summary>
    public sealed class McpServerLaunchSpec
    {
        public const string NpmPackage = "aker-mcp";

        public string Command { get; }
        public IReadOnlyList<string> Args { get; }

        public McpServerLaunchSpec(string command, IReadOnlyList<string>? args = null)
        {
            Command = command ?? throw new ArgumentNullException(nameof(command));
            Args = args ?? Array.Empty<string>();
        }

        /// <summary>
        /// <c>npx -y aker-mcp@latest</c>: the npm launcher, which follows new releases by itself. Needs Node 18+.
        ///
        /// On Windows it goes through <c>cmd /c</c>: npx is a .cmd script there, and a client that starts its server
        /// without a shell (Claude Code, Claude Desktop, any Node-based client) gets ENOENT for a bare "npx".
        /// </summary>
        public static McpServerLaunchSpec NpxLatest(string? profile = null, bool? windows = null)
        {
            var args = new List<string>();
            var onWindows = windows ?? RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            if (onWindows) args.AddRange(new[] { "/c", "npx" });
            args.AddRange(new[] { "-y", NpmPackage + "@latest" });
            AppendProfile(args, profile);
            return new McpServerLaunchSpec(onWindows ? "cmd" : "npx", args);
        }

        /// <summary><c>aker-mcp</c>, the .NET global tool (<c>dotnet tool install -g AkerMcp.Server</c>).</summary>
        public static McpServerLaunchSpec DotnetTool(string? profile = null)
        {
            var args = new List<string>();
            AppendProfile(args, profile);
            return new McpServerLaunchSpec("aker-mcp", args);
        }

        /// <summary>An executable by absolute path: the installed server (<see cref="ServerInstall"/>) or one of your own.</summary>
        public static McpServerLaunchSpec Executable(string executablePath, string? profile = null)
        {
            var args = new List<string>();
            AppendProfile(args, profile);
            return new McpServerLaunchSpec(executablePath, args);
        }

        /// <summary><c>dotnet run --project &lt;Server&gt;</c>: for someone working on the server itself.</summary>
        public static McpServerLaunchSpec FromSource(string projectDirectory, string configuration, string? profile = null)
        {
            var args = new List<string> { "run", "--project", projectDirectory, "-c", configuration, "--verbosity", "quiet", "--nologo" };
            if (!string.IsNullOrWhiteSpace(profile) && !string.Equals(profile, "standard", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("--");
                AppendProfile(args, profile);
            }
            return new McpServerLaunchSpec("dotnet", args);
        }

        /// <summary>The <c>--profile</c> in a list of arguments; "standard" (the server's default) when there is none.</summary>
        public static string ProfileOf(IReadOnlyList<string> args)
        {
            for (var i = 0; i < args.Count; i++)
            {
                if (args[i] == "--profile" && i + 1 < args.Count) return args[i + 1].ToLowerInvariant();
                if (args[i].StartsWith("--profile=", StringComparison.Ordinal)) return args[i].Substring("--profile=".Length).ToLowerInvariant();
            }
            return "standard";
        }

        public override string ToString() => Args.Count == 0 ? Command : $"{Command} {string.Join(" ", Args)}";

        private static void AppendProfile(List<string> args, string? profile)
        {
            if (!string.IsNullOrWhiteSpace(profile) &&
                !string.Equals(profile, "standard", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("--profile");
                args.Add(profile!.Trim().ToLowerInvariant());
            }
        }
    }

    public sealed class ClientConfigStatus
    {
        public bool FileExists { get; set; }
        public bool IsConfigured { get; set; }
        public string? Command { get; set; }
        public IReadOnlyList<string> Args { get; set; } = Array.Empty<string>();
        public string? ParseError { get; set; }

        /// <summary>
        /// Projects that have their own entry (Claude Code's "local" scope, under <c>projects</c> in ~/.claude.json).
        /// Inside those projects that entry wins over the user-level one.
        /// </summary>
        public IReadOnlyList<string> ProjectEntries { get; set; } = Array.Empty<string>();
    }

    public sealed class ConfigPatchResult
    {
        public bool Success { get; set; }
        public bool Changed { get; set; }
        public string ConfigPath { get; set; } = "";
        public string? BackupPath { get; set; }
        public string Message { get; set; } = "";
    }

    /// <summary>
    /// Reads, merges and removes the one MCP server entry (<c>game-engine</c>) in an AI client's JSON config and leaves
    /// everything else in the file as it was: other servers, other settings, characters outside ASCII.
    ///
    /// Before every write the file is copied to <c>&lt;file&gt;.aker-backup</c>, and the new content replaces it in one
    /// step (temp file + <see cref="File.Replace(string, string, string)"/>). Comments and trailing commas are accepted on
    /// read but cannot be written back; when the file had some, the result message says so and names the backup.
    ///
    /// Writing ~/.claude.json while Claude Code runs is safe: Claude Code re-reads the file before it saves its own
    /// changes (checked on 2026-11-21 with a probe key that survived a save of a running session).
    /// </summary>
    public static class McpConfigPatcher
    {
        public const string DefaultServerKey = "game-engine";

        private static readonly JsonDocumentOptions LenientRead = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

        private static readonly JsonDocumentOptions StrictRead = new JsonDocumentOptions();

        // These files are edited by hand. The default encoder would write "è" as è and ' as ' everywhere in
        // the file, including in entries AkerMCP never touched; nothing here is ever embedded in HTML.
        private static readonly JsonWriterOptions PrettyWriter = new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static ClientConfigStatus Inspect(AiClientDefinition client, string serverKey = DefaultServerKey)
        {
            var status = new ClientConfigStatus { FileExists = File.Exists(client.ConfigPath) };
            if (!status.FileExists)
                return status;

            try
            {
                var root = ReadRootObject(client.ConfigPath, out _);
                if (root[client.RootProperty] is JsonObject servers &&
                    servers[serverKey] is JsonObject entry)
                {
                    status.IsConfigured = true;
                    status.Command = entry["command"] is JsonValue c && c.TryGetValue<string>(out var command) ? command : null;
                    if (entry["args"] is JsonArray arr)
                    {
                        status.Args = arr
                            .Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "")
                            .ToArray();
                    }
                }

                if (client.ProjectsProperty != null && root[client.ProjectsProperty] is JsonObject projects)
                {
                    status.ProjectEntries = projects
                        .Where(p => p.Value is JsonObject project &&
                                    project[client.RootProperty] is JsonObject projectServers &&
                                    projectServers[serverKey] is JsonObject)
                        .Select(p => p.Key)
                        .ToArray();
                }
            }
            catch (Exception ex)
            {
                status.ParseError = ex.Message;
            }

            return status;
        }

        public static ConfigPatchResult Configure(
            AiClientDefinition client,
            McpServerLaunchSpec spec,
            string serverKey = DefaultServerKey,
            bool dryRun = false)
        {
            try
            {
                var exists = File.Exists(client.ConfigPath);
                var lostComments = false;
                var root = exists ? ReadRootObject(client.ConfigPath, out lostComments) : new JsonObject();

                if (root[client.RootProperty] is not JsonObject servers)
                {
                    if (root[client.RootProperty] != null)
                    {
                        return new ConfigPatchResult
                        {
                            Success = false,
                            ConfigPath = client.ConfigPath,
                            Message = $"'{client.RootProperty}' in {client.ConfigPath} is not a JSON object: fix it by hand, AkerMCP will not guess."
                        };
                    }
                    servers = new JsonObject();
                    root[client.RootProperty] = servers;
                }

                var newEntry = BuildEntry(spec, client.IncludeTypeStdio, servers[serverKey] as JsonObject);
                var existing = servers[serverKey] as JsonObject;
                var changed = existing == null || !JsonNode.DeepEquals(existing, newEntry);

                if (!changed)
                {
                    return new ConfigPatchResult
                    {
                        Success = true,
                        Changed = false,
                        ConfigPath = client.ConfigPath,
                        Message = $"{client.DisplayName} already starts {spec}."
                    };
                }

                if (dryRun)
                {
                    return new ConfigPatchResult
                    {
                        Success = true,
                        Changed = true,
                        ConfigPath = client.ConfigPath,
                        Message = $"[dry-run] Would set {client.RootProperty}.{serverKey} in {client.ConfigPath} to: {spec}"
                    };
                }

                servers[serverKey] = newEntry;
                var backupPath = WriteAtomically(client.ConfigPath, root, exists);

                return new ConfigPatchResult
                {
                    Success = true,
                    Changed = true,
                    ConfigPath = client.ConfigPath,
                    BackupPath = backupPath,
                    Message = $"Configured {client.DisplayName}. {client.RestartHint}" + LostCommentsNote(lostComments, backupPath)
                };
            }
            catch (Exception ex)
            {
                return new ConfigPatchResult
                {
                    Success = false,
                    ConfigPath = client.ConfigPath,
                    Message = $"Could not update {client.DisplayName} ({client.ConfigPath}): {ex.Message}"
                };
            }
        }

        public static ConfigPatchResult Remove(
            AiClientDefinition client,
            string serverKey = DefaultServerKey,
            bool dryRun = false)
        {
            try
            {
                if (!File.Exists(client.ConfigPath))
                {
                    return new ConfigPatchResult
                    {
                        Success = true,
                        Changed = false,
                        ConfigPath = client.ConfigPath,
                        Message = $"{client.DisplayName} has no config file: nothing to remove."
                    };
                }

                var root = ReadRootObject(client.ConfigPath, out var lossy);
                if (root[client.RootProperty] is not JsonObject servers || !servers.ContainsKey(serverKey))
                {
                    return new ConfigPatchResult
                    {
                        Success = true,
                        Changed = false,
                        ConfigPath = client.ConfigPath,
                        Message = $"{client.DisplayName} has no '{serverKey}' entry."
                    };
                }

                if (dryRun)
                {
                    return new ConfigPatchResult
                    {
                        Success = true,
                        Changed = true,
                        ConfigPath = client.ConfigPath,
                        Message = $"[dry-run] Would remove '{serverKey}' from {client.ConfigPath}."
                    };
                }

                servers.Remove(serverKey);
                var backupPath = WriteAtomically(client.ConfigPath, root, createBackup: true);

                return new ConfigPatchResult
                {
                    Success = true,
                    Changed = true,
                    ConfigPath = client.ConfigPath,
                    BackupPath = backupPath,
                    Message = $"Removed '{serverKey}' from {client.DisplayName}. {client.RestartHint}" + LostCommentsNote(lossy, backupPath)
                };
            }
            catch (Exception ex)
            {
                return new ConfigPatchResult
                {
                    Success = false,
                    ConfigPath = client.ConfigPath,
                    Message = $"Could not update {client.DisplayName} ({client.ConfigPath}): {ex.Message}"
                };
            }
        }

        private static string LostCommentsNote(bool lost, string? backupPath) =>
            lost && backupPath != null
                ? $" The file had comments or trailing commas, which JSON cannot keep: the original is in {backupPath}."
                : "";

        /// <summary>
        /// The entry to write. Keys AkerMCP does not manage (an "env" block, a client's own flags) are carried over from
        /// the entry being replaced, so re-applying never drops what the user added to it.
        /// </summary>
        private static JsonObject BuildEntry(McpServerLaunchSpec spec, bool includeTypeStdio, JsonObject? previous)
        {
            var entry = new JsonObject();
            if (includeTypeStdio)
                entry["type"] = "stdio";
            entry["command"] = spec.Command;
            var argsArray = new JsonArray();
            foreach (var arg in spec.Args)
                argsArray.Add(arg);
            entry["args"] = argsArray;

            if (previous != null)
            {
                foreach (var kv in previous)
                {
                    if (kv.Key == "type" || kv.Key == "command" || kv.Key == "args") continue;
                    entry[kv.Key] = kv.Value?.DeepClone();
                }
            }
            return entry;
        }

        private static JsonObject ReadRootObject(string path, out bool lossy)
        {
            lossy = false;
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
                return new JsonObject();

            var node = JsonNode.Parse(text, documentOptions: LenientRead);
            if (node is not JsonObject obj)
                throw new InvalidDataException($"the top-level JSON value in {path} is not an object.");

            // Parsed only leniently: what made the strict parse fail (comments, trailing commas) is gone on write.
            try { using (JsonDocument.Parse(text, StrictRead)) { } }
            catch (JsonException) { lossy = true; }
            return obj;
        }

        private static string? WriteAtomically(string path, JsonObject root, bool createBackup)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string? backupPath = null;
            if (createBackup && File.Exists(path))
            {
                backupPath = path + ".aker-backup";
                File.Copy(path, backupPath, overwrite: true);
            }

            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var ms = new MemoryStream())
                {
                    using (var writer = new Utf8JsonWriter(ms, PrettyWriter))
                    {
                        root.WriteTo(writer);
                    }
                    File.WriteAllText(tmp, Encoding.UTF8.GetString(ms.ToArray()) + Environment.NewLine, new UTF8Encoding(false));
                }

                if (File.Exists(path))
                {
                    // One step: a crash between a delete and a move would have left the client with no config at all.
                    File.Replace(tmp, path, null);
                }
                else
                {
                    File.Move(tmp, path);
                }
            }
            finally
            {
                if (File.Exists(tmp))
                {
                    try { File.Delete(tmp); } catch { /* ignore cleanup failure */ }
                }
            }

            return backupPath;
        }
    }
}
