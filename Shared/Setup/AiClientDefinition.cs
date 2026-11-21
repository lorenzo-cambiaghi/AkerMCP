using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace AkerMcp.Shared.Setup
{
    /// <summary>
    /// Describes one supported AI MCP client and where its user-level MCP configuration lives
    /// on the current operating system. Shared between the standalone server CLI (`aker-mcp setup`)
    /// and the editor plugins (Unity / Godot / Stride) so client paths live in one place.
    /// </summary>
    public sealed class AiClientDefinition
    {
        public string Id { get; }
        public string DisplayName { get; }
        public string ConfigPath { get; }
        public string DetectionPath { get; }
        public string RootProperty { get; }
        public bool IncludeTypeStdio { get; }
        public string RestartHint { get; }

        /// <summary>
        /// Where the client keeps per-project servers that override the user-level one ("projects" in ~/.claude.json,
        /// Claude Code's "local" scope), or null when it has none in this file.
        /// </summary>
        public string? ProjectsProperty { get; }

        public AiClientDefinition(
            string id,
            string displayName,
            string configPath,
            string detectionPath,
            string rootProperty = "mcpServers",
            bool includeTypeStdio = false,
            string restartHint = "",
            string? projectsProperty = null)
        {
            Id = id;
            DisplayName = displayName;
            ConfigPath = configPath;
            DetectionPath = detectionPath;
            RootProperty = rootProperty;
            IncludeTypeStdio = includeTypeStdio;
            RestartHint = string.IsNullOrEmpty(restartHint)
                ? $"Restart {displayName} to pick up the change."
                : restartHint;
            ProjectsProperty = projectsProperty;
        }

        /// <summary>
        /// True when either the config file already exists or its parent application folder is present.
        /// </summary>
        public bool IsInstalled =>
            !string.IsNullOrEmpty(ConfigPath) &&
            (File.Exists(ConfigPath) || Directory.Exists(DetectionPath));
    }

    public static class AiClientCatalog
    {
        /// <summary>All supported AI clients resolved for the current OS and user profile.</summary>
        public static IReadOnlyList<AiClientDefinition> All => BuildCatalog();

        public static AiClientDefinition? FindById(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            var key = id.Trim().ToLowerInvariant();
            foreach (var client in All)
            {
                if (string.Equals(client.Id, key, StringComparison.OrdinalIgnoreCase))
                    return client;
            }
            return null;
        }

        private static IReadOnlyList<AiClientDefinition> BuildCatalog()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

            var claudeDesktopDir = isWin
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude")
                : isMac
                    ? Path.Combine(home, "Library", "Application Support", "Claude")
                    : Path.Combine(home, ".config", "Claude");

            var vsCodeUserDir = isWin
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User")
                : isMac
                    ? Path.Combine(home, "Library", "Application Support", "Code", "User")
                    : Path.Combine(home, ".config", "Code", "User");

            var claudeCodePath = Path.Combine(home, ".claude.json");
            var claudeCodeDetect = Path.Combine(home, ".claude");
            var cursorDir = Path.Combine(home, ".cursor");
            var windsurfDir = Path.Combine(home, ".codeium", "windsurf");
            var antigravityDir = Path.Combine(home, ".gemini", "antigravity");

            return new[]
            {
                new AiClientDefinition(
                    id: "claude-code",
                    displayName: "Claude Code",
                    configPath: claudeCodePath,
                    detectionPath: claudeCodeDetect,
                    rootProperty: "mcpServers",
                    includeTypeStdio: true,
                    restartHint: "Start a new Claude Code session to pick up the change.",
                    projectsProperty: "projects"),

                new AiClientDefinition(
                    id: "claude-desktop",
                    displayName: "Claude Desktop",
                    configPath: Path.Combine(claudeDesktopDir, "claude_desktop_config.json"),
                    detectionPath: claudeDesktopDir,
                    rootProperty: "mcpServers",
                    includeTypeStdio: false,
                    restartHint: "Quit and reopen Claude Desktop to pick up the change."),

                new AiClientDefinition(
                    id: "cursor",
                    displayName: "Cursor",
                    configPath: Path.Combine(cursorDir, "mcp.json"),
                    detectionPath: cursorDir,
                    rootProperty: "mcpServers",
                    includeTypeStdio: false,
                    restartHint: "Reload Cursor (or open MCP Settings) to pick up the change."),

                new AiClientDefinition(
                    id: "windsurf",
                    displayName: "Windsurf",
                    configPath: Path.Combine(windsurfDir, "mcp_config.json"),
                    detectionPath: windsurfDir,
                    rootProperty: "mcpServers",
                    includeTypeStdio: false,
                    restartHint: "Reload Windsurf to pick up the change."),

                new AiClientDefinition(
                    id: "antigravity",
                    displayName: "Google Antigravity",
                    configPath: Path.Combine(antigravityDir, "mcp_config.json"),
                    detectionPath: antigravityDir,
                    rootProperty: "mcpServers",
                    includeTypeStdio: true,
                    restartHint: "Reload Antigravity MCP servers to pick up the change."),

                new AiClientDefinition(
                    id: "vscode",
                    displayName: "VS Code (Copilot)",
                    configPath: Path.Combine(vsCodeUserDir, "mcp.json"),
                    detectionPath: vsCodeUserDir,
                    rootProperty: "servers",
                    includeTypeStdio: true,
                    restartHint: "Reload VS Code to pick up the change.")
            };
        }
    }
}
