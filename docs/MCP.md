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

Claude Code, Codex, Antigravity CLI and Hermes can add AI Meter with their own `mcp add` command. Under **From the command line** in **Settings → AI apps**, the **Windows** and **WSL** buttons copy the command with your paths filled in. Paste it into the matching terminal, then restart the CLI.

AI Meter writes the relay at every start, so the path in the command exists as soon as AI Meter has run once. In the examples below, replace `<you>` with your Windows user name.

**Windows (PowerShell)**

```powershell
claude mcp add --scope user ai-meter '--' 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -NoProfile -NonInteractive -ExecutionPolicy Bypass -File 'C:\Users\<you>\AppData\Roaming\AIMeter\mcp.ps1'
codex mcp add ai-meter '--' 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -NoProfile -NonInteractive -ExecutionPolicy Bypass -File 'C:\Users\<you>\AppData\Roaming\AIMeter\mcp.ps1'
agy mcp add ai-meter 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' '--' -NoProfile -NonInteractive -ExecutionPolicy Bypass -File 'C:\Users\<you>\AppData\Roaming\AIMeter\mcp.ps1'
hermes mcp add ai-meter --command 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' --args -NoProfile -NonInteractive -ExecutionPolicy Bypass -File 'C:\Users\<you>\AppData\Roaming\AIMeter\mcp.ps1'
```

The quotes around `'--'` matter. When a CLI is installed with npm, PowerShell runs its `.ps1` shim and drops a bare `--`. The CLI would then read `-NoProfile` as one of its own options.

**WSL (bash)**

```bash
claude mcp add --scope user ai-meter -- /mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File 'C:\Users\<you>\AppData\Roaming\AIMeter\mcp.ps1'
codex mcp add ai-meter -- /mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File 'C:\Users\<you>\AppData\Roaming\AIMeter\mcp.ps1'
agy mcp add ai-meter /mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -- -NoProfile -NonInteractive -ExecutionPolicy Bypass -File 'C:\Users\<you>\AppData\Roaming\AIMeter\mcp.ps1'
hermes mcp add ai-meter --command '/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe' --args -NoProfile -NonInteractive -ExecutionPolicy Bypass -File 'C:\Users\<you>\AppData\Roaming\AIMeter\mcp.ps1'
```

Each command needs the right argument order:
- **Claude Code and Codex** take the whole server command after `--`.
- **Antigravity CLI** takes the program before `--` and its arguments after it. Its options go before the name `ai-meter`.
- **Hermes** takes the program with `--command` and its arguments with `--args`, which must come last: it keeps every following word, dashes included. Hermes tries the server before saving it in `config.yaml`, and asks before replacing an existing `ai-meter` entry. In a running Hermes, `/reload-mcp` loads it without a restart.
- `--scope user` makes the server available in every project. Without it, Claude Code adds it to the current project only. Antigravity CLI has no scope: its servers apply everywhere.

Check the connection with `claude mcp list`, `codex mcp get ai-meter`, `agy mcp list` or `hermes mcp test ai-meter`. Remove it with `claude mcp remove --scope user ai-meter`, `codex mcp remove ai-meter`, `agy mcp remove ai-meter` or `hermes mcp remove ai-meter`.

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
| `list_candidates` | Where a task can run now for free: free models that meet its needs, that a coding agent on this PC (Pi or OpenCode, on Windows or in any running WSL distribution) can call with its own key, and whose limits have room, in a fixed order; each pool's room; why every other model is left out; which agents were found but cannot be used; and, when asked, best-effort candidates built on the agents' own model lists. See below |
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
- Delegation runs only the agents whose start has been verified for their place, listed in [agent definitions](#agent-definitions): today Pi in WSL, and OpenCode in WSL and on Windows. It uses whichever of these are set up; none is required. An agent counts when its folder exists and its program is installed (a folder can outlive its agent). AI Meter checks the usual install folders, those of Node version managers (nvm, fnm, asdf, Volta) included, on Windows and in every running WSL distribution, through the distribution's file share, without starting anything inside WSL. `runtimes.found` lists the agents found, with `distro` in WSL.
- **`runtimes.unsupported`** lists the other agents found (by their folder or their program), so that a plugin can say "Codex is installed, but delegation does not support it yet". Each entry has `name`, `display_name`, `where`, `distro` in WSL, `reason` and `detail`, a sentence to show:

  | `reason` | Meaning |
  |---|---|
  | `no_dispatch` | Delegation does not support this agent (Codex, Claude Code, Gemini CLI, Antigravity CLI, Cursor Agent, Crush, Hermes) |
  | `place_not_verified` | It supports the agent, but not in this place (Pi on Windows) |
  | `program_not_found` | Its settings folder is here, but its program is not in the usual folders |
  | `not_set_up` | Its program is here, but it has no settings folder yet |

  An agent in `unsupported` is never a candidate.
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
| `no_runtime` | No supported agent is set up on Windows, nor in a running WSL distribution (see `runtimes.unsupported`) |
| `runtime_has_no_key` | No agent that is set up has a key of its own for this service |
| `key_discovery_off` | Key discovery is turned off, so the agents' keys cannot be checked |
| `rate_limited` | The service answered "too many requests"; `until` is when AI Meter may ask again |
| `exhausted` | A counter the service reports is used up; `until` is its reset |

AI Meter sees only the rate limits of its own reads and the counters services report. Rate limits an agent hits during a task are counted when its plugin releases the task's lease.

**`warnings`** appears on a candidate (and in `describe_dispatch`) when the plugin must watch for something AI Meter cannot see:

| Warning | Meaning |
|---|---|
| `openrouter_byok_fallback` | OpenRouter can retry a `:free` request that hit the free route's limit on a provider key the user linked to OpenRouter (BYOK), billed to that provider account. The catalogue's zero price cannot show this. Watch `output.cost` and stop the agent at the first request priced above zero, or ask the user to unlink BYOK keys before delegating |

### Best-effort candidates

Some services list their models without the facts delegation needs. NVIDIA's list gives only ids; the Gemini API's gives no tool support; neither gives prices. Their models are therefore never ordinary candidates. The coding agents often know more: their own model lists give tool support, the context window, the reply size and the price they charge.

With `include_best_effort: true`, `list_candidates` also returns **`best_effort`**: models the service's list leaves short that an agent with a key of its own lists **at no price**, filling in what the task needs.

- **They are a separate list, never mixed with `candidates`.** A plugin uses one only when the user agreed beforehand. The task may fail, stop or meet limits nobody has stated, so failures are handled case by case, without automatic retries on the same model.
- **Without the flag**, `best_effort_available` counts them, and the models keep their exclusions.
- **Order and room.** Best-effort candidates follow the same order as ordinary ones, and their pools appear in `pools`.
- **What each entry adds:**
  - `warnings` starting with `best_effort`;
  - `estimated`, which gives `source` (`agent:pi`), the agent's `tools`, `reasoning`, `context_tokens`, `max_output_tokens` and `price_per_million`, and `unverified`: what rests on the agent's word (`price`, `tools`, `context`, `output`, and `limits` when the service states none).
- **The agent must list the model at no price.** A model the agent prices is excluded as `agent_lists_price`, because the plugin stops an agent at its first priced request. For example, Pi prices Gemini Flash at its paid-tier rate, whatever the project's tier.
- **A zero price in an agent's list is not proof that the model is free.** It means only that the agent will not stop it as paid.

Checked on 2026-11-02 against public lists:
- all 21 NVIDIA models in Pi's list have tool calling according to models.dev;
- of the 386 OpenRouter models Pi lists, 377 have tools on OpenRouter. Pi marks 8 of the others as image models (`type: "image"`), which are not counted as having tools here. The one Pi lists as a chat model, the `openrouter/fusion` router, is the kind of exception best effort is for.

### Running a task: `describe_dispatch` and capacity leases

AI Meter starts nothing. A coordinator plugin runs the agent ([ADR 0002](adr/0002-delegation-through-coordinator-plugins.md)), following these steps:
1. Choose with `list_candidates`.
2. `describe_dispatch` for the candidate. It reserves nothing, so a plugin can describe first and claim only for an agent it can start.
3. `claim_capacity` with the candidate's `service`, `model` and `runtime`.
4. Start `program` with `arguments` and `environment`, without a shell. Write the task to stdin, then close it.
5. Read the output as `output` describes. `renew_capacity` during long tasks.
6. `release_capacity` with the outcome.

The [AI Meter delegation plugin](../plugins/ai-meter/README.md) for Claude Code and Codex follows these steps.

**`describe_dispatch`** takes the candidate (`service`, `model`, `runtime`, and `where` and `distro` to pick its place), `mode` (`read_only` by default, or `modify`), `workspace`, and `from`, which is where the plugin runs (`windows` by default, or `wsl`).
- It describes only agents and places with a verified dispatch: Pi in WSL, and OpenCode in WSL and on Windows. Anything else answers `supported: false` with the reason.
- Before that, an `error` says which of these went wrong: an unknown `runtime` (with the ids AI Meter knows); AI Meter still reading its first data; a model not in the service's current list; an agent delegation does not support; no usable agent set up here; key discovery turned off; or an agent without a key of its own for the service.
- `version_verified` and `notes` flag an agent version outside the verified range.
- **On Windows**, the agent's native program is started directly: the `.exe` found on the PATH, or the one inside its npm package (OpenCode). Never a `.cmd` or `.ps1`, which need a shell: without a native program the answer is `supported: false`.
- **In WSL, from Windows**, it is `wsl.exe -d <distro> -e env PATH=… <agent>`, in the candidate's own distribution. `wsl.exe --` is never used, because it would hand the arguments to a Linux shell. The PATH holds the agent's folder, then the folder of the `node` it needs when that is elsewhere (an nvm install, say), then the system folders. An agent npm installed whose `node` cannot be found answers `supported: false`.
- **OpenCode is isolated** from the user's skills and project configuration, and its title model is set to the same free model. Otherwise it would call a paid one.
- **`output`** says how to tell the end, the outcome (Pi's exit code stays 0 on errors), the requests, the tokens, the rate limits and `cost`: where the agent reports each request's price (Pi: `usage.cost.total` of assistant messages; OpenCode: `step_finish.part.cost`). Both agents price requests from their own model list, so a value above zero means a paid request and the plugin should stop; zero does not prove the request was free, as an agent cannot see a BYOK fallback billed by the provider.
- **`warnings`** repeats the candidate's warnings, such as `openrouter_byok_fallback`.
- **`stop`** says how to stop the agent: `taskkill /T /F` on Windows, which also ends an agent started in WSL.

**Leases.**
- `claim_capacity` refuses when the pool has no room. The answer includes the pool's `headroom`, with `blocking.reason`: `exhausted`, `rate_limited` or `leased`.
- A lease lasts `ttl_seconds` (default 1800, at most 7200) unless renewed. Unreleased leases expire with an unknown outcome.
- `release_capacity` takes `outcome`, `requests`, `rate_limited`, `retry_after_seconds` and the tokens. A rate limit closes the pool until `retry_after_seconds`, or for a minute.
- Active leases count in every `headroom` (`leased`). Released outcomes fill `observed` in `list_free_models`, with `coverage: "ai_meter_leases_only"`: tasks run without leases are not counted.
- AI Meter keeps the leases in `%APPDATA%\AIMeter\leases.json`. It stores only pool, service, model, agent, client label, times and outcome counters, for 30 days. Never task text, code, logs or keys.

### Agent definitions

The coding agents AI Meter knows are data, like services: `agents/*.json`, built into AI Meter, plus your own files in `%APPDATA%\AIMeter\agents`. A file there with the id of a built-in agent replaces it; a new id adds an agent. AI Meter reads them at start. A definition:

```json
{
  "id": "opencode",
  "name": "OpenCode",
  "order": 10,
  "programs": { "windows": ["opencode"], "wsl": ["opencode"] },
  "folders": [".local/share/opencode", ".config/opencode"],
  "keys": [
    { "file": ".local/share/opencode/auth.json", "path": "{id}.key" },
    { "file": ".config/opencode/opencode.json", "path": "provider.{id}.options.apiKey" }
  ],
  "package": "opencode-ai",
  "dispatch": {
    "places": {
      "wsl": { "versions": ["1.x"] },
      "windows": { "versions": ["1.x"], "executables": ["node_modules/opencode-windows-x64/bin/opencode.exe"] }
    },
    "arguments": {
      "read_only": ["run", "--pure", "--format", "json", "--agent", "plan", "--dir", "{workspace}", "-m", "{provider}/{model}"],
      "modify": ["run", "--pure", "--format", "json", "--agent", "build", "--dir", "{workspace}", "-m", "{provider}/{model}"]
    },
    "environment": { "OPENCODE_DISABLE_AUTOUPDATE": "1", "OPENCODE_CONFIG_CONTENT": { "small_model": "{provider}/{model}" } },
    "output": { "format": "jsonl", "end": "the process exits", "cost": "step_finish.part.cost of each step" },
    "notes": ["Close stdin after writing the task: with stdin open OpenCode waits for more."]
  }
}
```

| Field | Meaning |
|---|---|
| `id`, `name` | The runtime id tools use (`a-z`, `0-9`, `-`) and the name shown |
| `order` | Position in key discovery and lists, lower first (default 1000) |
| `programs` | Program names per place (`windows`, `wsl`), without an extension. On Windows `.exe`, `.cmd`, `.bat` and `.ps1` are tried, in that order |
| `folders` | Settings folders relative to the home: any one shows the agent is set up |
| `keys` | Where the agent keeps API keys: a home-relative `file` and a JSON `path`, with `{id}` for the service's aliases (`auth.ids` in its service definition), or a `file` with `"format": "env"`, a dotenv file of `NAME=value` lines read by the variable names in the service's `auth.env` (Hermes keeps its keys this way) |
| `package` | npm package, to read the version without running the agent; an agent npm installed in WSL also needs `node` |
| `models` | The agent's own model list, for [best-effort candidates](#best-effort-candidates): `format` (`pi` or `models.dev`), `file` under `{home}/` or `{package}/` with `{provider}` for the service's alias, and optional `versions` whose layout was checked. Pi: `{package}/node_modules/@earendil-works/pi-ai/dist/providers/data/{provider}.json` for 1.x; OpenCode: `{home}/.cache/opencode/models.json` |
| `dispatch` | How a plugin starts it. Without it, the agent is reported in `runtimes.unsupported` |
| `dispatch.places` | The verified places, each with `versions` (`"1.x"` matches every 1.* version, `"1.4.0"` only that one), optional `executables` (native Windows programs inside the npm package) and optional own `arguments` and `environment` |
| `dispatch.arguments` | The argument list per mode, `read_only` and `modify`. `read_only` must be enforced by what the agent is given, such as Pi's `--tools` list or OpenCode's per-agent `permission` in `OPENCODE_CONFIG_CONTENT` (see `agents/opencode.json`), never by an agent's name and defaults alone |
| `dispatch.environment` | Variables to set. A value written as a JSON object is serialised after its placeholders are filled, so a model id cannot change its structure |
| `dispatch.output`, `dispatch.notes` | Returned as they are in `describe_dispatch` |

Placeholders, filled in one pass: `{provider}` (the alias the agent keeps the key under), `{model}` and `{workspace}` (`.` inside WSL when started from Windows, where `wsl.exe --cd` already sets the folder). Agents without a verified dispatch are still useful: their folders and programs show them in `runtimes.unsupported`, and their `keys` let AI Meter find keys for the services it meters.

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
