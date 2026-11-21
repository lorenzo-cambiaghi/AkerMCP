# aker-mcp

Launcher for **[AkerMCP](https://github.com/lorenzo-cambiaghi/AkerMCP)**, the live MCP server for **Unity**, **Godot 4** and **Stride Game Studio**: it gives Claude Code, Claude Desktop, Cursor, Windsurf, Copilot and Antigravity live scene inspection and editing, Roslyn C# execution, viewport screenshots, automated playtesting and placeholder sprite and audio generation, all running on your machine.

## Connect your AI clients

```bash
npx -y aker-mcp@latest setup
```

finds the AI clients installed on this computer and adds AkerMCP to each one, keeping a `.aker-backup` copy of every file it changes. `npx -y aker-mcp@latest doctor` then checks the clients and the running editors. `setup --help` lists the options (one client, a tool profile, a dry run, uninstall).

## Or add it by hand

```bash
# Claude Code (macOS, Linux)
claude mcp add --scope user game-engine -- npx -y aker-mcp@latest
# Claude Code (Windows: npx is a .cmd script, which a client cannot start without a shell)
claude mcp add --scope user game-engine -- cmd /c npx -y aker-mcp@latest
```

```json
{
  "mcpServers": {
    "game-engine": { "command": "npx", "args": ["-y", "aker-mcp@latest"] }
  }
}
```

On Windows the entry is `"command": "cmd"` with `"args": ["/c", "npx", "-y", "aker-mcp@latest"]`. Add `"--profile", "full"` to the arguments for sprite and audio authoring, `playtest`, build tools and `ui_action`.

## What it does

On first use it downloads the standalone `AkerMcp.Server` of its own version from the [GitHub release](https://github.com/lorenzo-cambiaghi/AkerMCP/releases) (about 30 MB) into a per-user folder, the same one the Unity plugin uses (`%LOCALAPPDATA%\AkerMcp\server`, `~/Library/Application Support/AkerMcp/server`, `~/.local/share/AkerMcp/server`), and runs it on stdio. With `@latest` it follows new releases: when one lands, the next start runs the server already on disk and downloads the new one in the background for the start after. `npx -y aker-mcp@latest update` downloads it right away.

`AKER_MCP_HOME` moves the folder; `AKER_MCP_BINARY` runs a server binary of your own. Nothing is ever written to stdout but MCP itself.

## The editor plugin

AkerMCP also needs its plugin inside your editor: see the [Quick Start](https://github.com/lorenzo-cambiaghi/AkerMCP#quick-start-recommended).
