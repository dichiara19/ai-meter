## What changed in 0.8.6

- **AI apps can read AI Meter (MCP).** AI Meter now includes a read-only [MCP](https://modelcontextprotocol.io) server. Connected AI apps and coding agents can ask it:
  - how much room your limits have;
  - which sessions are running;
  - which models are free;
  - what you spent today;
  - which local models are loaded;
  - which service has the most room left.

  It never shares keys, tokens or account identities. Session titles are shared only when you turn on *Share session titles*.
- **Connect apps in one click.** **Settings → AI apps** lists the apps it finds on this PC and in WSL: Claude Desktop, Claude Code, Codex, Gemini CLI, OpenCode, Pi, Cursor and Visual Studio Code. Turn an app on, then restart it.
  - AI Meter changes only its own `ai-meter` entry and keeps a copy of the previous file.
  - **Copy configuration** gives the entry for any other app that can start a local MCP server.
- **Or from the command line.** For Claude Code, Codex and Gemini CLI, the **Windows** and **WSL** buttons copy the agent's own `mcp add` command with your paths filled in.

See [docs/MCP.md](https://github.com/dichiara19/ai-meter/blob/main/docs/MCP.md) for the tools and the commands.

## Download

- **AIMeter-script.zip** (recommended): unzip and run `Start AI Meter.cmd`. Works with Smart App Control enabled.
- **AIMeter.zip**: unzip and run `AIMeter.exe`. The executable is unsigned and may be blocked by Smart App Control.

To update, exit AI Meter, replace the files in its existing folder and start it again. Settings and usage history are retained. Requires Windows 10 or 11 with .NET Framework 4.8.
