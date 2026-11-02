# AI Meter for AI apps (MCP)

AI Meter includes a [Model Context Protocol](https://modelcontextprotocol.io) server. With it, AI apps and coding agents can read the data AI Meter already shows: how much room your limits have, which sessions are running, which models are free and what you spent today. A coding agent can then check its own limits before a long task, or choose a free model.

The server is **read-only** and never returns API keys, tokens or account identities. Session titles often repeat your first request, so they are shared only when you turn on *Share session titles*.

## Connecting an app

Open **Settings → AI apps**. AI Meter lists the supported apps it finds on this PC, and those inside WSL while WSL is running. Turn an app on to connect it, then restart that app.

| App | File AI Meter edits | Entry |
|---|---|---|
| Claude Desktop | `%APPDATA%\Claude\claude_desktop_config.json` | `mcpServers` |
| Claude Code | `~/.claude.json` (user scope) | `mcpServers`, `type: stdio` |
| Codex (CLI, IDE extension and app) | `~/.codex/config.toml` | `[mcp_servers.ai-meter]` |
| Gemini CLI | `~/.gemini/settings.json` | `mcpServers` |
| OpenCode | `~/.config/opencode/opencode.json` | `mcp`, `type: local` |
| Pi | `~/.pi/agent/mcp.json` | `mcpServers`; requires `pi install npm:pi-mcp-adapter` |
| Cursor | `~/.cursor/mcp.json` | `mcpServers` |
| Visual Studio Code | `%APPDATA%\Code\User\mcp.json` | `servers`, `type: stdio` |

Each change:
- adds or removes only the `ai-meter` entry;
- keeps the previous file as `<file>.aimeter-backup`;
- leaves alone any file that is not plain JSON, for example JSON with comments. AI Meter then shows the entry so you can add it by hand.

For any other app that can start a local MCP server, **Copy configuration** gives the standard `mcpServers` entry.

ChatGPT desktop is not listed. Reports disagree on whether it starts local servers: when it does, use **Copy configuration**.

## From the command line

Claude Code, Codex and Gemini CLI can add AI Meter with their own `mcp add` command. Under **From the command line** in **Settings → AI apps**, the **Windows** and **WSL** buttons copy the command with your paths filled in. Paste it into the matching terminal, then restart the CLI.

AI Meter writes the relay at every start, so the path in the command exists as soon as AI Meter has run once. In the examples below, replace `<you>` with your Windows user name.

**Windows (PowerShell)**

```powershell
claude mcp add --scope user ai-meter '--' 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -NoProfile -NonInteractive -ExecutionPolicy Bypass -File 'C:\Users\<you>\AppData\Roaming\AIMeter\mcp.ps1'
codex mcp add ai-meter '--' 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -NoProfile -NonInteractive -ExecutionPolicy Bypass -File 'C:\Users\<you>\AppData\Roaming\AIMeter\mcp.ps1'
gemini mcp add --scope user ai-meter 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' '--' -NoProfile -NonInteractive -ExecutionPolicy Bypass -File 'C:\Users\<you>\AppData\Roaming\AIMeter\mcp.ps1'
```

The quotes around `'--'` matter. When a CLI is installed with npm, PowerShell runs its `.ps1` shim and drops a bare `--`. The CLI would then read `-NoProfile` as one of its own options.

**WSL (bash)**

```bash
claude mcp add --scope user ai-meter -- /mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File 'C:\Users\<you>\AppData\Roaming\AIMeter\mcp.ps1'
codex mcp add ai-meter -- /mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File 'C:\Users\<you>\AppData\Roaming\AIMeter\mcp.ps1'
gemini mcp add --scope user ai-meter /mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -- -NoProfile -NonInteractive -ExecutionPolicy Bypass -File 'C:\Users\<you>\AppData\Roaming\AIMeter\mcp.ps1'
```

Each command needs the right argument order:
- **Claude Code and Codex** take the whole server command after `--`.
- **Gemini CLI** takes the program before `--` and its arguments after it.
- `--scope user` makes the server available in every project. Without it, Claude Code adds it to the current project only, and Gemini CLI adds it to `.gemini/settings.json` in the current folder.

Check the connection with `claude mcp list`, `codex mcp get ai-meter` or `gemini mcp list`. Remove it with `claude mcp remove --scope user ai-meter`, `codex mcp remove ai-meter` or `gemini mcp remove --scope user ai-meter`.

Other apps:
- **Visual Studio Code** has its own command. From `cmd`, run `code --add-mcp "{\"name\":\"ai-meter\",\"type\":\"stdio\",\"command\":\"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\",\"args\":[\"-NoProfile\",\"-NonInteractive\",\"-ExecutionPolicy\",\"Bypass\",\"-File\",\"C:\\Users\\<you>\\AppData\\Roaming\\AIMeter\\mcp.ps1\"]}"`. The switch in Settings is simpler.
- **OpenCode** asks its questions interactively (`opencode mcp add`).
- **Pi** and **Cursor** have no command. Use the switch in Settings for these.

## How it works

```
AI app ──stdio──▶ powershell.exe -File mcp.ps1 ──named pipe──▶ AI Meter (McpServer)
```

- **The relay.** Apps start `mcp.ps1`, a short PowerShell relay that AI Meter writes to `%APPDATA%\AIMeter`. It is not in the installation folder, so updates do not break connected apps.
  - It connects to the pipe `\\.\pipe\AIMeter.Mcp.<user>`, which only your Windows user can open.
  - If AI Meter is not running, the relay starts it.
  - Then it forwards one JSON-RPC message per line.
- **Apps in WSL.** They start the same Windows relay through WSL interop: `/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe`.
- **Where the answers come from.** The server answers from AI Meter's latest reading. Tools add no network requests and never start WSL.

## Tools

| Tool | Returns |
|---|---|
| `get_limits` | Each service's limits: percentage used and remaining, reset time, forecast run-out time, pace per hour, money limits |
| `list_sessions` | Running sessions of AI apps and coding agents: agent, status, model, provider; `busy_only` keeps only working ones |
| `list_free_models` | Free models with id, context window, tool and reasoning support and last free day; filters `tools` and `min_context` |
| `get_spend` | Today's models per service with cost, tokens and replies, and money limits such as budgets and credits |
| `list_local_models` | Models loaded by Ollama, LM Studio, llama.cpp and other local servers |
| `suggest_service` | Services ordered by the room left in their tightest limit, with their number of free models |

All tools except `list_local_models` accept an optional `service` (`claude`, `openai`, `copilot`, `openrouter`…).

Each result is returned twice: as `structuredContent`, and as the same JSON in a text block for clients that read only text. Times are local ISO 8601 and percentages range from 0 to 100.

## Limits

- The data is as fresh as AI Meter's last update: every 10 to 30 seconds while a surface is open, every two minutes otherwise.
- In the Microsoft Store package the relay is written to `~/.aimeter`, because a packaged app's AppData is private to the package.
