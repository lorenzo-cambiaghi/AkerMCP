# AkerMcp.Server

The live MCP server for **Unity**, **Godot 4** and **Stride Game Studio**, as a .NET global tool: it lets an AI client (Claude Code, Claude Desktop, Codex, Cursor, Windsurf, Copilot, Cline, Antigravity) inspect and edit the running editor's scene, run C# on its main thread, take screenshots and playtest, all on your machine.

```bash
dotnet tool install -g AkerMcp.Server
aker-mcp setup     # connects the AI clients installed on this computer (a .aker-backup of each file is kept)
aker-mcp doctor    # checks the clients and the running editors
```

`aker-mcp setup --help` lists the options. Update with `dotnet tool update -g AkerMcp.Server`.

AkerMCP also needs its plugin inside your editor: see the [Quick Start](https://github.com/lorenzo-cambiaghi/AkerMCP#quick-start-recommended).
