using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace AkerMcp.Shared.Setup
{
    public enum ConnectionKind
    {
        /// <summary>No game-engine entry in the client's user-level config.</summary>
        NotConfigured,
        /// <summary>The config file exists but is not valid JSON, even read leniently.</summary>
        Unreadable,
        /// <summary>The server installed by AkerMCP (<see cref="ServerInstall"/>), at a known version.</summary>
        Installed,
        /// <summary>The npm launcher, which follows new releases by itself.</summary>
        Npx,
        /// <summary>The .NET global tool.</summary>
        DotnetTool,
        /// <summary><c>dotnet run --project …</c>: someone working on the server.</summary>
        Source,
        /// <summary>A command of the user's own that AkerMCP does not manage.</summary>
        Custom,
        /// <summary>An entry that cannot start: its executable is gone, or a bare "npx" on Windows.</summary>
        Broken,
    }

    /// <summary>What a client's game-engine entry starts, read once and shared by `aker-mcp doctor` and the editor windows.</summary>
    public sealed class AiConnection
    {
        public ConnectionKind Kind { get; }

        /// <summary>The installed server's version, for <see cref="ConnectionKind.Installed"/>.</summary>
        public string? ServerVersion { get; }

        /// <summary>The tool profile the entry passes (standard when it passes none).</summary>
        public string Profile { get; }

        /// <summary>One line for a person: what runs, or why it cannot.</summary>
        public string Summary { get; }

        private AiConnection(ConnectionKind kind, string summary, string profile = "standard", string? serverVersion = null)
        {
            Kind = kind;
            Summary = summary;
            Profile = profile;
            ServerVersion = serverVersion;
        }

        public bool IsManaged => Kind == ConnectionKind.Installed || Kind == ConnectionKind.Npx;

        /// <summary>Whether this entry, as it stands, starts the server of exactly <paramref name="version"/>.</summary>
        public bool RunsVersion(string version) =>
            Kind == ConnectionKind.Installed && string.Equals(ServerVersion, version.TrimStart('v', 'V'), StringComparison.Ordinal);

        public static AiConnection Classify(ClientConfigStatus status) => Classify(status, ServerInstall.Root, RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        public static AiConnection Classify(ClientConfigStatus status, string installRoot, bool windows)
        {
            if (status.ParseError != null)
                return new AiConnection(ConnectionKind.Unreadable, "invalid JSON: " + status.ParseError);
            if (!status.IsConfigured || string.IsNullOrWhiteSpace(status.Command))
                return new AiConnection(ConnectionKind.NotConfigured, "not configured");

            var command = status.Command!;
            var args = status.Args;
            var profile = McpServerLaunchSpec.ProfileOf(args);
            var name = Path.GetFileNameWithoutExtension(command).ToLowerInvariant();

            var version = ServerInstall.VersionOf(command, installRoot);
            if (version != null)
            {
                return File.Exists(command)
                    ? new AiConnection(ConnectionKind.Installed, $"AkerMCP server {version}", profile, version)
                    : new AiConnection(ConnectionKind.Broken, $"the server it points to is gone ({command})", profile);
            }

            var launchesNpx = name == "npx" || (name == "cmd" && args.Any(a => string.Equals(a, "npx", StringComparison.OrdinalIgnoreCase)));
            if (launchesNpx && args.Any(a => a.StartsWith(McpServerLaunchSpec.NpmPackage, StringComparison.Ordinal)))
            {
                if (windows && name == "npx")
                    return new AiConnection(ConnectionKind.Broken,
                        "a bare \"npx\" does not start on Windows (it needs \"cmd /c npx\")", profile);
                return new AiConnection(ConnectionKind.Npx, "npx aker-mcp (follows new releases)", profile);
            }

            if (name == "aker-mcp")
                return new AiConnection(ConnectionKind.DotnetTool, ".NET global tool aker-mcp", profile);

            if (name == "dotnet" && args.Count > 0 && args[0] == "run")
                return new AiConnection(ConnectionKind.Source, "dotnet run (from source)", profile);

            if (Path.IsPathRooted(command) && !File.Exists(command))
                return new AiConnection(ConnectionKind.Broken, $"the file it starts is gone ({command})", profile);

            return new AiConnection(ConnectionKind.Custom, "your own command: " + command, profile);
        }
    }
}
