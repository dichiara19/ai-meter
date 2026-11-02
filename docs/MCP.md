# AI Meter for AI apps (MCP)

AI Meter includes a [Model Context Protocol](https://modelcontextprotocol.io) server. With it, AI apps and coding agents can read the data AI Meter already shows: how much room your limits have, which sessions are running, which models are free and what you spent today. A coding agent can then check its own limits before a long task, or choose a free model.

The server changes nothing on your PC or your accounts, with one exception: the three lease tools (`claim_capacity`, `renew_capacity`, `release_capacity`) write AI Meter's own `leases.json`, next to its settings, which other tools also update when they find a lease expired. It never returns API keys, tokens or account identities. Session titles often repeat your first request, so they are shared only when you turn on *Share session titles*.

## Connecting an app

Open **Settings → AI apps**. AI Meter lists the supported apps it finds on this PC, and those inside WSL while WSL is running. Turn an app on to connect it, then restart that app.

| App | File AI Meter edits | Entry |
|---|---|---|
| Claude Desktop | `%APPDATA%\Claude\claude_desktop_config.json` | `mcpServers` |
| Claude Code | `~/.claude.json` (user scope) | `mcpServers`, `type: stdio` |
| Codex (CLI, IDE extension and app) | `~/.codex/config.toml` | `[mcp_servers.ai-meter]` |
| Antigravity CLI (`agy`) | `~/.gemini/config/mcp_config.json` | `mcpServers`; an empty file counts as `{}` |
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

Antigravity CLI is listed when `~/.gemini/antigravity-cli` exists. Gemini CLI has been discontinued and is no longer listed; a `~/.gemini` folder on its own does not count. In headless runs (`agy -p`), agy asks no questions and rejects tools it has no rule for, so AI Meter's tools need an allow rule under `permissions.allow` in `~/.gemini/antigravity-cli/settings.json`, or in `--settings` for one run. In interactive sessions you can approve the calls when agy asks.

## From the command line

Claude Code, Codex and Antigravity CLI can add AI Meter with their own `mcp add` command. Under **From the command line** in **Settings → AI apps**, the **Windows** and **WSL** buttons copy the command with your paths filled in. Paste it into the matching terminal, then restart the CLI.

AI Meter writes the relay at every start, so the path in the command exists as soon as AI Meter has run once. In the examples below, replace `<you>` with your Windows user name.

**Windows (PowerShell)**

```powershell
claude mcp add --scope user ai-meter '--' 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -NoProfile -NonInteractive -ExecutionPolicy Bypass -File 'C:\Users\<you>\AppData\Roaming\AIMeter\mcp.ps1'
codex mcp add ai-meter '--' 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -NoProfile -NonInteractive -ExecutionPolicy Bypass -File 'C:\Users\<you>\AppData\Roaming\AIMeter\mcp.ps1'
agy mcp add ai-meter 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' '--' -NoProfile -NonInteractive -ExecutionPolicy Bypass -File 'C:\Users\<you>\AppData\Roaming\AIMeter\mcp.ps1'
```

The quotes around `'--'` matter. When a CLI is installed with npm, PowerShell runs its `.ps1` shim and drops a bare `--`. The CLI would then read `-NoProfile` as one of its own options.

**WSL (bash)**

```bash
claude mcp add --scope user ai-meter -- /mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File 'C:\Users\<you>\AppData\Roaming\AIMeter\mcp.ps1'
codex mcp add ai-meter -- /mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File 'C:\Users\<you>\AppData\Roaming\AIMeter\mcp.ps1'
agy mcp add ai-meter /mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -- -NoProfile -NonInteractive -ExecutionPolicy Bypass -File 'C:\Users\<you>\AppData\Roaming\AIMeter\mcp.ps1'
```

Each command needs the right argument order:
- **Claude Code and Codex** take the whole server command after `--`.
- **Antigravity CLI** takes the program before `--` and its arguments after it. Its options go before the name `ai-meter`.
- `--scope user` makes the server available in every project. Without it, Claude Code adds it to the current project only. Antigravity CLI has no scope: its servers apply everywhere.

Check the connection with `claude mcp list`, `codex mcp get ai-meter` or `agy mcp list`. Remove it with `claude mcp remove --scope user ai-meter`, `codex mcp remove ai-meter` or `agy mcp remove ai-meter`.

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
- **Where the answers come from.** The server answers from AI Meter's latest reading. A tool adds network requests only when the app asks for newer data (see [Fresh data](#fresh-data)). No tool ever starts WSL.

## Tools

| Tool | Returns |
|---|---|
| `get_limits` | Each service's limits: percentage used and remaining, reset time, forecast run-out time, pace per hour, money limits |
| `list_sessions` | Running sessions of AI apps and coding agents: agent, status, model, provider; `busy_only` keeps only working ones |
| `list_free_models` | Free models with id, context window, output limit, tool and reasoning support, last free day, how the price is known (`free`) and their limits (`limits`, see below); filters `tools`, `min_context`, `min_output_tokens` and `include_unconfirmed` |
| `get_spend` | Today's models per service with cost, tokens and replies, and money limits such as budgets and credits |
| `list_local_models` | Models loaded by Ollama, LM Studio, llama.cpp and other local servers |
| `list_candidates` | Where a task can run now for free: free models that meet its needs, that a coding agent on this PC (Pi, OpenCode or Crush) can call with its own key, and whose limits have room, in a fixed order; each pool's room; and why every other model is left out. See below |
| `describe_dispatch` | How to start a candidate: program, argument list and environment for a verified agent and place. The task goes on stdin, never in the arguments. See below |
| `claim_capacity`, `renew_capacity`, `release_capacity` | Cooperative capacity leases on a candidate's pool. These are the only tools that change anything: AI Meter's own lease book. See below |
| `suggest_service` | Services ordered by the room left in their tightest limit, with their number of free models |

All tools except `list_local_models` and `suggest_service` accept an optional `service` (`claude`, `openai`, `copilot`, `openrouter`…).

`suggest_service` lists services with known room first. A service that offers free models but has no readable limit comes after them, with `remaining_percent: null` and `quota: "unknown"`. Its room is unknown, not 100%. A service whose reading is out of date is marked `stale` and ranked after fresh ones with the same room.

Each result is returned twice: as `structuredContent`, and as the same JSON in a text block for clients that read only text. Times are local ISO 8601 and percentages range from 0 to 100.

### Free models and their limits

Each model from `list_free_models` says how its price is known and what limits a task on it meets. An agent can use this to decide how many tasks to start and where.

```json
"free": { "status": "confirmed_free", "kind": "zero_price", "source": "catalogue" },
"limits": {
  "per_request": { "context_tokens": { "value": 262144, "source": "catalogue" }, "max_output_tokens": { "value": 32768, "source": "catalogue" } },
  "pool": "openrouter:free",
  "rates": [
    { "metric": "requests_per_minute", "limit": 20, "used": null, "remaining": null, "scope": "account", "per_model": false,
      "source": "documented", "reference": "https://openrouter.ai/docs/api-reference/limits", "checked_at": "2026-11-02" },
    { "metric": "requests_per_day", "limit": 50, "used": 12, "remaining": 38, "scope": "account", "per_model": false,
      "source": "provider_reported", "coverage": "all_clients", "resets_at": "2026-11-03T01:00:00+01:00", "reset_zone": "UTC" }
  ],
  "headroom": { "blocking": null, "can_start_now": true, "max_new_jobs_now": 1 }
}
```

- **`free.status`.**
  - `confirmed_free`: the service's list states a zero price.
  - `unknown`: the service lists no prices, as with NVIDIA and Gemini. Whether such a model is free depends on the account or project, which the key cannot read. These models are listed only with `include_unconfirmed: true`, and `free.note` explains why.
- **`per_request`.** The context window and the reply size the service states for one request. Unknown values are `null`, and filters leave those models out.
- **`pool`.** The counter the limits draw from. Models with the same pool share it, so never add their limits up. `{model}` pools, such as Gemini's, are per model.
- **`rates`.** Each limit says where it comes from:

  | `source` | Meaning |
  |---|---|
  | `provider_reported` | Read from the service for this account, including what other apps used (`coverage: all_clients`) |
  | `documented` | Published by the service, with `reference` and `checked_at` |
  | `unknown` | The limit exists, but no number is available. It is never guessed |

  Daily limits give `resets_at` and their `reset_zone`, with daylight saving. Gemini's day ends at midnight Pacific time.
- **`headroom`.**
  - `blocking` names what stops a task now: the service asked AI Meter to wait (`rate_limited`), or a reported counter is used up (`exhausted`, with `until`).
  - Without a known concurrency limit, `max_new_jobs_now` is at most 1.
  - AI Meter decides how many tasks start. It does not pace the requests a running agent makes.
  - Counters of delegated tasks arrive with capacity leases, which coordinator plugins claim and release.

### Choosing where to run a task: `list_candidates`

`list_candidates` answers "where can this task run now, for free, and how many tasks can start?". It starts nothing and reserves nothing.

**Arguments:**
- `tools`, `min_context` and `min_output_tokens`: what the task needs;
- `service`: only this service;
- `cost_policy`: `free_only` (default) admits only models whose list states a zero price. `free_or_unconfirmed` also admits models whose price the service does not state, such as NVIDIA and Gemini models;
- `refresh` and `max_age_seconds`, as in the other tools.

**A candidate is a model, a runtime and a credential.**
- No particular agent is required. Delegation uses whichever supported agents are set up: Pi (`~/.pi/agent`), OpenCode (`~/.local/share/opencode` or `~/.config/opencode`) and Crush (`~/.config/crush` or `~/.local/share/crush`). An agent counts when its folder exists and its program is installed (a folder can outlive its agent). AI Meter checks the usual install folders on Windows, and in WSL while it is running, through the distribution's file share, without starting anything inside WSL. `runtimes.found` lists the agents found.
- A model is a candidate for an agent only when that agent already has its own key for the service: in its own files, or in the environment it starts with. That means this process's variables on Windows, or shell-profile exports in WSL. Each agent with a key is a separate candidate, and they all share the same pool.
- Keys saved in AI Meter do not count: AI Meter never hands a key to a runtime.
- With *Use API keys already on the PC* turned off, AI Meter cannot check the agents' keys, and says so.
- `credential` is an opaque id (`cred_…`): the same key gives the same id while AI Meter runs. It is never the key, its file or the account.

**Order.**
1. Candidates whose pool has known room come first, most room first. The room is the remaining share of the pool's tightest limit with a known value.
2. Candidates whose room is unknown come next, one task at a time.
3. Ties are broken by service, model and place, so the same request always gives the same order.

**`pools`** gives each pool once, with `max_new_jobs_now`, `remaining_share` and `binding_limit`. Models in one pool share it, so plan by pool, not by model.

**`excluded`** groups the other models by service and reason, with `until` when the reason ends at a known time:

| `reason` | Meaning |
|---|---|
| `price_not_confirmed` | The service does not state a zero price, and the policy is `free_only` |
| `no_tools`, `tools_unknown` | The task needs tool calls; the model has none, or its list does not say |
| `context_too_small`, `context_unknown`, `output_too_small`, `output_unknown` | The task needs more than the model takes, or the limit is not stated |
| `no_runtime` | No supported agent is set up on Windows, nor in a running WSL |
| `runtime_has_no_key` | No agent that is set up has a key of its own for this service |
| `key_discovery_off` | Key discovery is turned off, so the agents' keys cannot be checked |
| `rate_limited` | The service answered "too many requests"; `until` is when AI Meter may ask again |
| `exhausted` | A counter the service reports is used up; `until` is its reset |

AI Meter sees only the rate limits of its own reads and the counters services report. Rate limits an agent hits during a task are counted once AI Meter runs the task itself.

### Running a task: `describe_dispatch` and capacity leases

AI Meter starts nothing. A coordinator plugin runs the agent ([ADR 0002](adr/0002-delegation-through-coordinator-plugins.md)), following these steps:
1. Choose with `list_candidates`.
2. `claim_capacity` with the candidate's `service`, `model` and `runtime`.
3. `describe_dispatch` for the same candidate.
4. Start `program` with `arguments` and `environment`, without a shell. Write the task to stdin, then close it.
5. Read the output as `output` describes. `renew_capacity` during long tasks.
6. `release_capacity` with the outcome.

**`describe_dispatch`** takes the candidate, `mode` (`read_only` by default, or `modify`), `workspace`, and `from`, which is where the plugin runs (`windows` by default, or `wsl`).
- It describes only agents and places verified live: Pi in WSL, and OpenCode in WSL and on Windows. Anything else answers `supported: false` with the reason.
- `version_verified` and `notes` flag an agent version outside the verified range.
- **On Windows**, OpenCode's native program is started directly, never its `.cmd` or `.ps1`.
- **In WSL, from Windows**, it is `wsl.exe -e env PATH=… <agent>`. `wsl.exe --` is never used, because it would hand the arguments to a Linux shell.
- **OpenCode is isolated** from the user's skills and project configuration, and its title model is set to the same free model. Otherwise it would call a paid one.
- **`output`** says how to tell the end, the outcome (Pi's exit code stays 0 on errors), the requests, the tokens and the rate limits.
- **`stop`** says how to stop the agent: `taskkill /T /F` on Windows, which also ends an agent started in WSL.

**Leases.**
- `claim_capacity` refuses when the pool has no room. The answer includes the pool's `headroom`, with `blocking.reason`: `exhausted`, `rate_limited` or `leased`.
- A lease lasts `ttl_seconds` (default 1800, at most 7200) unless renewed. Unreleased leases expire with an unknown outcome.
- `release_capacity` takes `outcome`, `requests`, `rate_limited`, `retry_after_seconds` and the tokens. A rate limit closes the pool until `retry_after_seconds`, or for a minute.
- Active leases count in every `headroom` (`leased`). Released outcomes fill `observed` in `list_free_models`, with `coverage: "ai_meter_leases_only"`: tasks run without leases are not counted.
- AI Meter keeps the leases in `%APPDATA%\AIMeter\leases.json`. It stores only pool, service, model, agent, client label, times and outcome counters, for 30 days. Never task text, code, logs or keys.

## Fresh data

By default, tools answer from AI Meter's latest reading and make no network requests. AI Meter's own reading happens:
- every 10 to 30 seconds while a surface is open, and every two minutes otherwise;
- for a service's limits, at that service's own interval, usually every 5 minutes.

An app that needs newer data asks for it:

| Argument | Tools | Effect |
|---|---|---|
| `max_age_seconds` | `get_limits`, `get_spend`, `suggest_service`, `list_free_models` | Reads again only the services whose data is older than this. Preferred |
| `refresh` | the same tools | Reads every service again, except those read in the last minute |
| `refresh` | `list_sessions`, `list_local_models` | Reads local sessions and servers again. No network requests |

`list_free_models` reads the free-model lists. The other tools read the usage limits.

A refresh:
- goes only to the endpoints AI Meter already uses for each service you turned on;
- uses AI Meter's usual collection, so apps asking at the same time share one reading;
- asks each service at most once a minute;
- never asks a service before the time it gave when it answered "too many requests" (`429`, with or without `Retry-After`);
- waits up to 20 seconds. After that, the tool answers with the previous reading, and the new one arrives in a later call.

Every result of the four network tools includes `freshness`, one entry per service:

```json
"freshness": {
  "claude": { "state": "fresh", "read_at": "2026-11-02T12:00:05+01:00", "refresh": "refreshed" },
  "openrouter": { "state": "stale", "reason": "cached_reading", "read_at": "…", "refresh": "backoff", "retry_after": "…" }
}
```

| Field | Values |
|---|---|
| `state` | `fresh`; `stale` (an older reading kept after the service could not be read); `unavailable` (no reading at all) |
| `reason` | Why the data is not fresh: `cached_reading`, `sign_in_needed`, `no_reading`, `no_model_list` |
| `read_at` | When the data was read from the service |
| `refresh` | `not_requested`; `cached` (recent enough); `refreshed`; `unchanged` (asked, but the service gave nothing newer); `too_soon` (asked less than a minute ago); `backoff` (rate-limited by the service); `paused` (the PC is locked or asleep); `timed_out`; `unavailable` |
| `retry_after` | The earliest time another request can reach the service |

`list_sessions` and `list_local_models` report `refresh` once, at the top level, when it was asked for.

## Limits

- Without `refresh` or `max_age_seconds`, the data is as fresh as AI Meter's last reading (see [Fresh data](#fresh-data)).
- In the Microsoft Store package the relay is written to `~/.aimeter`, because a packaged app's AppData is private to the package.
