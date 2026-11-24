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
| `list_candidates` | Where a task can run now for free: free models that meet its needs, that a coding agent on this PC (Pi or OpenCode, on Windows or in any running WSL distribution) can call with its own key, and whose limits have room, most suitable first with the reason for each place; each pool's room; why every other model is left out; which agents were found but cannot be used; and, when asked, best-effort candidates built on the agents' own model lists, free-tier candidates (Cloudflare Workers AI) and Claude Code or Codex on the user's subscription plans. Also names the optional [managed copies of Pi](#managed-pi), and says whether WSL runs and Git for Windows is found. See below |
| `describe_dispatch` | How to start a candidate: program, argument list and environment for a verified agent and place. The task goes on stdin, never in the arguments. See below |
| `claim_capacity`, `renew_capacity`, `release_capacity` | Cooperative capacity leases on a candidate's pool, with the budget of a free tier. These are the only tools that change anything: AI Meter's own lease book. See below |
| `suggest_service` | Services ordered by the room left in their tightest limit, with their number of free models |

All tools except `list_local_models` and `suggest_service` accept an optional `service` (`claude`, `openai`, `copilot`, `openrouter`…).

In `get_limits`, a model's own limit (such as `Week Fable`) sits inside the plan's limit of the same length (`Week`). `remaining_percent` is the room that can actually be used, so when the plan's limit has less room it is that one, and `used_percent` and `remaining_percent` do not add up to 100. The limit then carries `capped_by` with the name of the limit that decided.

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
  - `confirmed_free`: the service's list states a zero price (`kind: "zero_price"`), or the service documents a free allocation (`kind: "free_tier"`, see [Free tiers](#free-tiers-cloudflare-workers-ai)).
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
- `include_free_tier`: also admit free-tier models, which are priced but free within a daily allocation ([Free tiers](#free-tiers-cloudflare-workers-ai)). Off by default, whatever the `cost_policy`;
- `from`: where the caller runs, `windows` or `wsl`. For the same model, an agent in that place comes first ([Order](#order));
- `include_plan`: also admit Claude Code and Codex on the user's subscription plans, never pay-as-you-go ([Subscription plans](#subscription-plans)). Off by default, whatever the `cost_policy`;
- `coordinator`: the service the caller itself runs on (`claude`, `openai`…). A plan on that service is never offered;
- `refresh` and `max_age_seconds`, as in the other tools.

**A candidate is a model, a runtime and a credential.**
- Delegation runs only the agents whose start has been verified for their place, listed in [agent definitions](#agent-definitions): today Pi in WSL, OpenCode in WSL and on Windows, and Claude Code and Codex on a plan in WSL ([Subscription plans](#subscription-plans)), and, if the user installed them, the [managed Pi](#managed-pi) on Windows and inside WSL. It uses whichever of these are set up; none is required. An agent counts when its folder exists and its program is installed (a folder can outlive its agent). AI Meter checks the usual install folders, those of Node version managers (nvm, fnm, asdf, Volta) included, on Windows and in every running WSL distribution, through the distribution's file share, without starting anything inside WSL. `runtimes.found` lists the agents found, with `distro` in WSL. Each managed Pi appears there as a runtime of its own, with `managed: true`, and `runtimes.managed` describes the places it can be installed in ([Managed Pi](#managed-pi)).
- **`runtimes.unsupported`** lists the other agents found (by their folder or their program), so that a plugin can say "Gemini CLI is installed, but delegation does not support it yet". Each entry has `name`, `display_name`, `where`, `distro` in WSL, `reason` and `detail`, a sentence to show:

  | `reason` | Meaning |
  |---|---|
  | `no_dispatch` | Delegation does not support this agent (Gemini CLI, Antigravity CLI, Cursor Agent, Crush, Hermes) |
  | `place_not_verified` | It supports the agent, but not in this place (Pi, Claude Code and Codex on Windows) |
  | `program_not_found` | Its settings folder is here, but its program is not in the usual folders |
  | `not_set_up` | Its program is here, but it has no settings folder yet |

  An agent in `unsupported` is never a candidate.
- A model is a candidate for an agent only when that agent already has its own key for the service: in its own files, or in the environment it starts with. That means this process's variables on Windows, or shell-profile exports in WSL. Each agent with a key is a separate candidate, and they all share the same pool.
- Keys saved in AI Meter do not count: AI Meter never hands a key to a runtime. The one exception is the managed Pi, whose key file holds only the keys the user chose to copy in Settings ([Managed Pi](#managed-pi)).
- With *Use API keys already on the PC* turned off, AI Meter cannot check the agents' keys, and says so.
- `credential` is an opaque id (`cred_…`): the same key gives the same id while AI Meter runs. It is never the key, its file or the account.

#### Order

Every candidate's pool can start a task now; a pool that cannot is in `excluded`. Candidates on a subscription plan come after free ones, unless the plan's room is about to be lost ([Subscription plans](#subscription-plans)). Among free candidates, AI Meter puts the most suitable first, by the first of these keys that differs:

1. **The user's preference.** Models marked *Preferred* in Settings › AI apps › Delegated tasks come first, models marked *Avoided* last. An avoided model stays a candidate: it is used when nothing else starts, or when the coordinator pins it.
2. **Room, in two bands.** Pools with at least 20% of their tightest limit left, or with unknown room, come before pools with less. Above that, room does not reorder models: a pool at 70% and one at 40% count the same.
3. **The model's track record on this PC.** Over the last 30 days of released leases: completed tasks, minus failed or timed-out ones, minus leases that met a rate limit. The highest score comes first; a model never tried scores 0, so a model that keeps failing or being rate limited drops below untried ones. A cancelled lease counts only its rate limit, if it met one; an expired lease, never released, is not counted.
4. **Reply size.** A model whose stated reply size is below 16,384 tokens comes after the others: that is too short for most coding tasks.
5. **Total weights.** No service list states a model's size. AI Meter takes the number of parameters in its published weights from Hugging Face when the list names the model's repository (OpenRouter's `hugging_face_id`), otherwise the size written in its id (`gemma-4-31b-it`, `mixtral-8x7b`). Models of unknown size come first, as closed models often are large; then the largest first.
6. **Context window**, largest first.
7. **Output speed measured here**: output tokens per second of the model's completed tasks over 30 days, tools included. Fastest first, then models never timed.
8. Service and model id, so the same request always gives the same order.

For the same model, an agent of the user's comes before a copy AI Meter keeps ([Managed Pi](#managed-pi)), whichever agent it is (Pi, OpenCode…); then, between the two managed copies, the one in the place the user prefers (setting `ManagedPiPrefer`: inside WSL unless the user chose Windows in Settings); then the agent that sends fewer tokens with every request (`request_tokens` in [agent definitions](#agent-definitions): Pi about 4,600, OpenCode about 9,600, against the same free quota); then, with `from`, the agent in the caller's place, which starts without the `wsl.exe` hop; then agent id, place and distribution.

The same snapshot, request, leases, preferences and known sizes give the same order.

**`rank`** on each candidate says why it stands where it is:

```json
"rank": {
  "why": "Not tried here yet; 561B weights (Hugging Face); 1,000,000 context; pi sends about 4,600 tokens per request; runs where the caller runs.",
  "preference": "neutral",
  "room": "enough",
  "track_record": { "days": 30, "completed": 0, "failed": 0, "rate_limited": 0, "score": 0, "output_tokens_per_second": null },
  "small_reply": false,
  "parameters": 560524578816,
  "parameters_source": "huggingface",
  "request_tokens": 4600,
  "same_place": true
}
```

`preference` is `preferred`, `neutral` or `avoided`; `room` is `enough` or `low`; `parameters_source` is `huggingface` or `model_id`, and `parameters` is null when unknown; `same_place` is present only with `from`. The context window and reply size are in `limits.per_request`.

**`pools`** gives each pool once, with `max_new_jobs_now`, `remaining_share` and `binding_limit`. Models in one pool share it, so plan by pool, not by model.

**`excluded`** groups the other models by service and reason, with `until` when the reason ends at a known time:

| `reason` | Meaning |
|---|---|
| `price_not_confirmed` | The service does not state a zero price, and the policy is `free_only` |
| `paid_plan_required` | A model the service offers only on its paid plan, as some of Cloudflare's: a free account is refused, and AI Meter cannot read the plan, so it is never offered |
| `free_tier_not_allowed` | A free-tier model, and the request did not set `include_free_tier` |
| `agent_price_unknown` | A free-tier model that no agent with a key lists at a price, so the plugin could not see what a task spends |
| `allowance_unknown` | A free tier whose use today is unknown, for example while its usage cannot be read: no task starts on a budget nobody knows |
| `no_tools`, `tools_unknown` | The task needs tool calls; the model has none, or its list does not say |
| `context_too_small`, `context_unknown`, `output_too_small`, `output_unknown` | The task needs more than the model takes, or the limit is not stated |
| `no_runtime` | No supported agent is set up on Windows, nor in a running WSL distribution (see `runtimes.unsupported`) |
| `runtime_has_no_key` | No agent that is set up has a key of its own for this service |
| `key_discovery_off` | Key discovery is turned off, so the agents' keys cannot be checked |
| `rate_limited` | The service answered "too many requests"; `until` is when AI Meter may ask again |
| `exhausted` | A counter the service reports is used up, or a free tier has nothing left beyond its reserve; `until` is its reset |

AI Meter sees only the rate limits of its own reads and the counters services report. Rate limits an agent hits during a task are counted when its plugin releases the task's lease.

**`warnings`** appears on a candidate (and in `describe_dispatch`) when the plugin must watch for something AI Meter cannot see:

| Warning | Meaning |
|---|---|
| `free_tier_budget` | The model is priced and free only within a daily allocation. Stop the agent as soon as its requests cost more than the `budget.max_cost` that `claim_capacity` returns, and release the lease with their `cost` |
| `openrouter_byok_fallback` | OpenRouter can retry a `:free` request that hit the free route's limit on a provider key the user linked to OpenRouter (BYOK), billed to that provider account. The catalogue's zero price cannot show this. Watch `output.cost` and stop the agent at the first request priced above zero, or ask the user to unlink BYOK keys before delegating |

### Subscription plans

Many users also pay for a Claude or ChatGPT plan, whose 5-hour and weekly windows often reset with room left. With `include_plan`, `list_candidates` also offers that room: **Claude Code on the Claude plan** and **Codex on the ChatGPT plan** (service `openai`), each with its own sign-in ([ADR 0003](adr/0003-delegation-on-subscription-plans.md)). A task on a plan never draws on pay-as-you-go billing.

**When a plan is offered.** All of these must hold, and each failure is an `excluded` reason:

| `reason` | Meaning |
|---|---|
| `plan_not_requested` | The request did not set `include_plan` |
| `plan_off_in_settings` | The user has not allowed this plan in Settings › AI apps › Delegated tasks (off by default) |
| `coordinator_unknown` | The request did not say which service the coordinator runs on, so its own plan could not be left out |
| `same_plan_as_coordinator` | The coordinator runs on this plan: a task there would save nothing. AI Meter cannot tell two accounts of one service apart, and does not try |
| `plan_usage_unknown` | No reading of the plan's windows, or one older than 15 minutes: room is never assumed |
| `plan_sign_in_not_confirmed` | The reading does not show a plan sign-in: Claude's usage endpoint, or `chatgpt` in Codex's `account/read` (an API key, or a reading from logs, does not count) |
| `plan_extra_usage_on` | Claude's extra usage is on: past the plan's limits it bills. Turn it off on claude.ai to delegate |
| `plan_credits_on` | Codex has credits (a balance, or unlimited): past the plan's limits it spends them |
| `plan_reserve` | A window is at or below the share the user keeps (by default 40% of the 5-hour window, 25% of the weekly one); `until` is when it resets |
| `plan_models_unknown` | Codex's model list (`~/.codex/models_cache.json`) could not be read |

The windows are the 5-hour one, the weekly one, and the weekly one of the model when the plan has it (Claude's "Opus" window binds Opus only). A plan takes one task at a time (pool `<service>:plan`), and its leases and rate limits count as for free pools.

**Models.** Claude Code offers `sonnet`, `opus`, `fable` and `haiku`, in that order (its definition's list); Codex the models its own cache lists for people to pick, in Codex's order. Plan models must meet the task's `tools`, `min_context` and `min_output_tokens` like free ones, with the same reasons, and a limit nobody states fails a stated need (`context_unknown`, `output_unknown`). The stated limits: Codex's cache gives each model's context window; Claude Code's definition gives `sonnet`'s context (1,000,000) and reply size (128,000), as Claude Code itself reported them on 2026-11-12, and nothing for the other aliases.

**Order.** Free candidates come first and the plan is the fallback, except when a plan's room is about to be lost: a 5-hour window resetting within the hour, or a weekly one within a day, with room above the reserve. Such a plan comes before every free candidate. Among a plan's candidates its own model order holds. `rank.plan` is `expiring` or `fallback`, and `rank.why` says which.

**What a plan candidate adds:**

```json
"billing": "included_in_plan",
"plan": {
  "service": "openai", "read_at": "…",
  "windows": [ { "window": "5h", "remaining_percent": 62, "reserve_percent": 40, "resets_at": "…" },
               { "window": "7d", "remaining_percent": 80, "reserve_percent": 25, "resets_at": "…" } ],
  "binding_window": "5h", "expiring": false,
  "coverage": "the plan's windows count everything the account uses, on every app and PC"
}
```

`binding_window` is the window with the least room above its reserve. `claim_capacity` checks the plan again and returns the same `plan`, so the report can say where the task drew from.

**Agents and places.** Verified in WSL on 2026-11-12: Claude Code 2.1.322 and Codex 0.163.0. On Windows, Claude Code had no sign-in to check with, and Codex's read-only sandbox refused to read the workspace, so both are `place_not_verified` there.

### Best-effort candidates

Some services list their models without the facts delegation needs. NVIDIA's list gives only ids; the Gemini API's gives no tool support; neither gives prices. Their models are therefore never ordinary candidates. The coding agents often know more: their own model lists give tool support, the context window, the reply size and the price they charge.

With `include_best_effort: true`, `list_candidates` also returns **`best_effort`**: models the service's list leaves short that an agent with a key of its own lists **at no price**, filling in what the task needs.

- **They are a separate list, never mixed with `candidates`.** A plugin uses one only when the user agreed beforehand. The task may fail, stop or meet limits nobody has stated, so failures are handled case by case, without automatic retries on the same model.
- **Without the flag**, `best_effort_available` counts them, and the models keep their exclusions.
- **Order and room.** Best-effort candidates follow the same [order](#order) as ordinary ones, with the agent's context window and reply size where the service states none, and their pools appear in `pools`.
- **What each entry adds:**
  - `warnings` starting with `best_effort`;
  - `estimated`, which gives `source` (`agent:pi`), the agent's `tools`, `reasoning`, `context_tokens`, `max_output_tokens` and `price_per_million`, and `unverified`: what rests on the agent's word (`price`, `tools`, `context`, `output`, and `limits` when the service states none).
- **The agent must list the model at no price.** A model the agent prices is excluded as `agent_lists_price`, because the plugin stops an agent at its first priced request, after it was paid. For example, Pi prices Gemini Flash at its paid-tier rate, whatever the project's tier. The same holds for ordinary candidates: a model the service lists as free is not offered through an agent whose own list prices it, and is excluded as `agent_lists_price` when no agent with a key lists it at no price.
- **A zero price in an agent's list is not proof that the model is free.** It means only that the agent will not stop it as paid.

Checked on 2026-11-02 against public lists:
- all 21 NVIDIA models in Pi's list have tool calling according to models.dev;
- of the 386 OpenRouter models Pi lists, 377 have tools on OpenRouter. Pi marks 8 of the others as image models (`type: "image"`), which are not counted as having tools here. The one Pi lists as a chat model, the `openrouter/fusion` router, is the kind of exception best effort is for.

### Free tiers: Cloudflare Workers AI

Cloudflare prices every Workers AI model in Neurons, but the first 10,000 Neurons of each UTC day are free, on the Workers Free and Paid plans alike. Past them, the Free plan refuses requests and the Paid plan bills $0.011 per 1,000. A task on these models is therefore free only while it stays within what is left today.

- **The list.** AI Meter reads the models from [models.dev](https://models.dev)'s public catalogue every 12 hours, with no key. It is the catalogue Pi and OpenCode price Cloudflare's models from, so the ids and prices match what the agents report. Each model has `free: { "status": "confirmed_free", "kind": "free_tier" }` and the pool `cloudflare-workers-ai:neurons`.
- **The limit.** The pool's `neurons_per_day` limit is 10,000, with today's use read from Cloudflare's analytics (`provider_reported`), which can lag a few minutes. The documented 300 requests per minute is listed too.
- **Consent.** They are candidates only with `include_free_tier: true`, never best effort.
- **The agent must price them.** A free-tier model is offered only through an agent whose own model list prices it, the opposite of free models. Otherwise the agent would report a cost of zero and the plugin could not see what the task spends (`agent_price_unknown`). The [managed Pi](#managed-pi) has no model list AI Meter can read, so these models are not offered through it.
- **The budget.** `headroom.budget` in `pools` and in `list_free_models`, and `budget` in the answer of `claim_capacity`:

  ```json
  "budget": { "metric": "neurons_per_day", "unit": "neurons", "limit": 10000, "used": 2500, "reserve": 1000,
              "spendable": 6500, "max_cost": 0.0715, "currency": "USD", "usd_per_unit": 0.000011, "leased_cost_today": 0, "resets_at": "…" }
  ```

  - `used` is the larger of Cloudflare's count and the cost released with today's leases (`leased_cost_today`, converted to Neurons), so a task that just ended counts before Cloudflare reports it. The two are never added up.
  - `reserve` (1,000 Neurons) is never offered: it absorbs the request that crosses the limit and the analytics lag.
  - `max_cost` is what the agent's requests may cost, in US dollars, before the plugin stops the agent. The plugin stops it after the request that crosses it, so one request can still go past: on the Paid plan, that request is billed.
  - Without a reading of today's use the budget is unknown, and the pool is closed (`allowance_unknown`). With nothing left beyond the reserve it is `exhausted` until midnight UTC.

### Running a task: `describe_dispatch` and capacity leases

AI Meter starts nothing. A coordinator plugin runs the agent ([ADR 0002](adr/0002-delegation-through-coordinator-plugins.md)), following these steps:
1. Choose with `list_candidates`.
2. `describe_dispatch` for the candidate. It reserves nothing, so a plugin can describe first and claim only for an agent it can start.
3. `claim_capacity` with the candidate's `service`, `model` and `runtime`.
4. Start `program` with `arguments` and `environment`, without a shell. Write the task to stdin, then close it.
5. Read the output as `output` describes. `renew_capacity` during long tasks. For a free tier, stop the agent once its requests cost more than the lease's `budget.max_cost`.
6. `release_capacity` with the outcome, and the `cost` the agent reported.

The [AI Meter delegation plugin](../plugins/ai-meter/README.md) for Claude Code and Codex follows these steps.

**`describe_dispatch`** takes the candidate (`service`, `model`, `runtime`, and `where` and `distro` to pick its place), `mode` (`read_only` by default, or `modify`), `workspace`, `from`, which is where the plugin runs (`windows` by default, or `wsl`), `managed` and, for a plan candidate, `coordinator`. For a managed copy, `managed: true` with `where` (and `distro`) picks the copy; without `where`, the preferred one is described.
- It describes only agents and places with a verified dispatch: Pi in WSL, OpenCode in WSL and on Windows, and Claude Code and Codex on a plan in WSL, and the managed Pi on Windows and inside WSL. Anything else answers `supported: false` with the reason.
- **A plan agent** ([Subscription plans](#subscription-plans)) comes with `billing: "included_in_plan"`, `environment_policy: "clean"` and a `sign_in_check`:
  - `environment_policy: "clean"`: start the agent with only what a program needs (system folders, home, PATH) plus `environment`, never the plugin's own environment, so no API key, base URL or coordinator session variable reaches it. From Windows into WSL the description already uses `env -i HOME=… LANG=C.UTF-8 PATH=…`.
  - `sign_in_check`: `program` and `arguments` of the agent's own status command, in the same place and environment, and what shows a plan sign-in: `json_field` equal to `expect` in its JSON (Claude Code: `authMethod` is `claude.ai`), or `text` in its output (Codex: "Logged in using ChatGPT", on stderr). Read only that; start the agent only when it holds.
  - Claude Code runs with `--safe-mode --restricted --strict-mcp-config` (no user `CLAUDE.md`, skills, plugins, hooks, MCP servers or settings files, so no `apiKeyHelper`), never `--bare`, which accepts only API keys. Codex runs with `--ignore-user-config`, `forced_login_method="chatgpt"`, `skills.include_instructions=false`, `project_doc_max_bytes=0` and its plugins, apps, hooks, memories and sub-agents disabled.
  - `output.billing` says how the agent's events show billed usage: Claude Code's `apiKeySource` other than `none`, or a `rate_limit_event` with `isUsingOverage`. Stop the agent then. Codex's events say nothing about billing: its guard is `forced_login_method` and AI Meter's `plan_credits_on`.
  - An `error` says when the plan is not allowed in Settings, the coordinator is missing or on the same plan, or the plan does not offer the model.
- Before that, an `error` says which of these went wrong: an unknown `runtime` (with the ids AI Meter knows); AI Meter still reading its first data; a model not in the service's current list; an agent delegation does not support; no usable agent set up here; key discovery turned off; or an agent without a key of its own for the service.
- A model id, workspace, distribution, provider alias or program path that starts with `-`, or holds a control character (a newline or NUL, say), answers `supported: false`: it would be read as one of the agent's options, or break the line. Values are never escaped, since the program starts without a shell and each value is its own argument.
- `version_verified` and `notes` flag an agent version outside the verified range.
- **On Windows**, the agent's native program is started directly: the `.exe` found on the PATH, or the one inside its npm package (OpenCode). Never a `.cmd` or `.ps1`, which need a shell: without a native program the answer is `supported: false`.
- **In WSL, from Windows**, it is `wsl.exe -d <distro> -e env PATH=… <agent>`, in the candidate's own distribution. `wsl.exe --` is never used, because it would hand the arguments to a Linux shell. The PATH holds the agent's folder, then the folder of the `node` it needs when that is elsewhere (an nvm install, say), then the system folders. An agent npm installed whose `node` cannot be found answers `supported: false`. So does an agent whose key is only exported in a WSL shell profile: `env` reads no profile, and the key is never passed on the command line instead.
- **In WSL, from WSL**, the agent's Linux path is started directly. It means something only in the plugin's own distribution, so the plugin skips candidates in other distributions.
- **OpenCode is isolated** from the user's skills and project configuration, and its title model is set to the same free model. Otherwise it would call a paid one.
- **`output`** says how to tell the end, the outcome (Pi's exit code stays 0 on errors), the requests, the tokens, the rate limits and `cost`: where the agent reports each request's price (Pi: `usage.cost.total` of assistant messages; OpenCode: `step_finish.part.cost`). Both agents price requests from their own model list, so a value above zero means a paid request and the plugin should stop; zero does not prove the request was free, as an agent cannot see a BYOK fallback billed by the provider.
- **`warnings`** repeats the candidate's warnings, such as `openrouter_byok_fallback`.
- **`commands`** says what the agent runs commands with when started with these `arguments`: `none`, `bash` or `powershell`. It is present only when the agent's definition says so (today, the managed Pi).
- **The managed Pi.** `managed` is a boolean: the candidate's `runtime.managed`. A copy AI Meter keeps is described, and leased by `claim_capacity`, only with `managed: true`; left out or false, the tools speak of the user's own installation. A plugin that does not know the copies, and so would start one without its environment policy and without removing its keys from the answers, is therefore never given one. `where` (and `distro`) pick the copy, in both tools; when `where` is omitted the answer is for the preferred copy, as ordered by `list_candidates`. For a managed copy the answer has `managed: true`, `managed_config`, `commands` and, for the copy on Windows in `modify` mode, `alternatives` when it is bound to a Git Bash. `managed_config` is the copy's configuration folder as the plugin can read it from where it runs, for the key file and its `keys-revision`: a Windows path for the copy on Windows; for the copy inside WSL a `\wsl.localhost\<distribution>\…` share path from Windows, and a Linux path from WSL. The copy on Windows:

  ```json
  {
    "supported": true, "runtime": "pi", "where": "windows", "managed": true, "version": "1.5.2", "version_verified": true,
    "mode": "modify", "program": "C:\\Users\\<you>\\AppData\\Roaming\\AIMeter\\managed\\pi\\1.5.2\\pi.exe",
    "arguments": ["-p", "--mode", "json", "--no-session", "--no-extensions", "--no-mcp", "--no-context-files", "--no-skills", "--no-prompt-templates", "--tools", "read,powershell,edit,write", "--provider", "<provider>", "--model", "<model>"],
    "environment": { "PI_CODING_AGENT_DIR": "C:\\Users\\<you>\\AppData\\Roaming\\AIMeter\\managed\\pi\\config", "PI_OFFLINE": "1", "PI_SKIP_VERSION_CHECK": "1", "PI_TELEMETRY": "0" },
    "commands": "powershell",
    "alternatives": [
      { "shell": "git_bash", "arguments": ["-p", "--mode", "json", "--no-session", "--no-extensions", "--no-mcp", "--no-context-files", "--no-skills", "--no-prompt-templates", "--tools", "read,bash,edit,write", "--provider", "<provider>", "--model", "<model>"] }
    ]
  }
  ```

  In `read_only` mode `commands` is `none` and there are no `alternatives`: that task needs no shell. In `modify` mode `alternatives` holds `git_bash` only while the copy is bound to a Git Bash (its settings name a `bash.exe` that exists); without it the list is empty and the copy has the PowerShell tool only. The rules for choosing the shell are in [Managed Pi](#managed-pi). The copy inside WSL is described as the user's own Pi in WSL is, with `commands` `none` in `read_only` mode and `bash` in `modify` mode (the distribution's own bash), no `alternatives` and `environment_policy: "no_credentials"`.
- **`stop`** says how to stop the agent: `taskkill /T /F` on Windows, which also ends an agent started in WSL.

**Leases.**
- `claim_capacity` refuses when the pool has no room. The answer includes the pool's `headroom`, with `blocking.reason`: `exhausted`, `rate_limited` or `leased`.
- A lease lasts `ttl_seconds` (default 1800, at most 7200) unless renewed. Unreleased leases expire with an unknown outcome.
- `claim_capacity` for a free-tier pool also returns its `budget` (see [Free tiers](#free-tiers-cloudflare-workers-ai)).
- `claim_capacity` for a plan candidate takes `coordinator`, checks the plan's windows again (a reserve reached since `list_candidates` refuses it with `blocking.reason: "plan_reserve"`) and returns `billing` and the `plan` the task draws on.
- `release_capacity` takes `outcome`, `requests`, `rate_limited`, `retry_after_seconds`, the tokens and `cost`, in US dollars as the agent priced the requests. A rate limit closes the pool until `retry_after_seconds`, or for a minute. The cost counts against a free tier's budget at once.
- Active leases count in every `headroom` (`leased`). Released outcomes fill `observed` in `list_free_models`, with `coverage: "ai_meter_leases_only"`: tasks run without leases are not counted.
- Released outcomes are also counted per model: completed, failed (or timed out), rate limited, and the output tokens and time of completed tasks. They are the track record that orders candidates ([Order](#order)).
- AI Meter keeps the leases in `%APPDATA%\AIMeter\leases.json`. It stores only pool, service, model, agent, client label, times and outcome counters (the cost among them), for 30 days. Never task text, code, logs or keys.
- Model sizes from Hugging Face are kept in `%APPDATA%\AIMeter\model-sizes.json`: the repository, its number of parameters and when it was checked.

### Managed Pi

A user with no agent of their own can let AI Meter keep a copy of Pi for delegation ([ADR 0004](adr/0004-managed-pi-for-delegation.md), [ADR 0005](adr/0005-managed-pi-inside-wsl.md)), on Windows, inside WSL or both. The default stays "use the agent you have": nothing is downloaded unless the user asks.

**What it is.** Pi's official standalone build (MIT licence, [earendil-works/pi](https://github.com/earendil-works/pi)), one pinned release, 1.5.2, in two archives. Each copy is installed and removed on its own.

| | On Windows | Inside WSL |
|---|---|---|
| Archive | `pi-windows-x64.zip`, 59 MB, about 131 MB unpacked | `pi-linux-x64.tar.gz`, 58,922,566 bytes, about 125 MB unpacked |
| Lives in | `managed\pi` under `%LOCALAPPDATA%\AIMeter` (GitHub build) | `~/.aimeter/managed/pi` in the Linux home of the distribution AI Meter is set to (one distribution) |
| Unpacked by | AI Meter | the distribution's own `tar`, through `wsl.exe` |
| Commands | PowerShell, or bash when Git for Windows is found | the distribution's bash |
| Folders | Windows folders only | Windows folders (through `/mnt/…`) and folders inside WSL |
| Reachable | always | only while the distribution runs |
| Dispatch place | `managed` | `managed_wsl` |

The Windows build is x64; on Windows on ARM it runs under emulation.

**Who installs it.** Only the user, with the buttons in Settings › AI apps › Delegated tasks, which also remove each copy. No MCP tool installs, updates or removes anything, and none returns a key. A coordinator that finds a copy missing tells the user where to set it up (`runtimes.managed[].set_up_in`) and does not try to.
- AI Meter downloads the pinned release from `github.com` (GitHub redirects to its release storage host), checks it against a SHA-256 kept in its own sources and discards any other file. The archive is hashed through one file handle held without sharing, with a new file and a new staging folder for each attempt; leftovers of earlier attempts are deleted and never promoted. A newer Pi is adopted only by a new AI Meter version, after the dispatch of both copies is verified.
- The copy on Windows is unpacked to `managed\pi\<version>` under `%LOCALAPPDATA%\AIMeter` for the GitHub build: the local part of the profile, which stays on this PC, where `%APPDATA%` can be copied to a server and to other PCs with a roaming profile, and the key file is plain text. The Microsoft Store build uses `%USERPROFILE%\.aimeter`, because a packaged app's AppData is private to the package. It is outside `PATH`, and nothing else on the PC is changed.
- **Inside WSL**, the archive is downloaded and checked on Windows, then the verified bytes are fed to the distribution's own `tar` through `wsl.exe`, which unpacks them with the files' permissions into a new staging folder; a rename promotes it. This is the one case in which AI Meter starts a program inside WSL, and only during an installation the user asked for (it makes the configuration folder private with `chmod` first); no MCP call and no task does it. The distribution must be running: AI Meter never starts WSL, and says so.

**Its own configuration and keys.**
- Each copy runs with its own folder, `config` next to the program, passed as `PI_CODING_AGENT_DIR` (the `{config}` placeholder of its definition). It never reads or writes the user's `~/.pi`. Inside WSL the folder has mode 700, so other Linux users cannot read the keys.
- The user chooses once, in Settings, the services whose key is copied; the choice applies to every installed and reachable copy, each of which gets its own `config\auth.json` and `keys-revision`, under the rules below. Only services Pi has a built-in provider for can be ticked (the `providers` list of the definition). The file is plain text, as in any Pi installation, where AI Meter's own key store is encrypted for the Windows user. Unticking the service, removing the key in AI Meter or removing the copy deletes it from there.
- While WSL is not running the copy inside it is not offered and its files are left as they are. Through a WSL share a file cannot be replaced in one step (seen live on 2026-11-24), so there AI Meter copies the new content over the old, and a reader may see it half written for an instant: the revision rule below makes the plugin withhold output on anything it cannot read.
- No MCP tool returns a key. `describe_dispatch` gives the path of the configuration folder, which is not a secret.
- A service is offered through the copy only while it is ticked in Settings: a key still in the file after a failed deletion gives no access. The key file, and a half-written `auth.json.new`, are deleted when nothing is ticked, when the copy is not installed, and at every start of AI Meter; removing the copy deletes the key files first.
- Only a plain key is copied. Pi reads a value that starts with `!` as a command to run and one that names an environment variable as a reference, and fills in `${NAME}` inside a value too, so a value starting with `!`, with a `$` anywhere, with white space or a control character, or shorter than eight characters is not copied and its service cannot be ticked. A key in the file that is no longer the one AI Meter has (changed or removed since) gives no access either.
- Both copies start with `environment_policy: "no_credentials"`: the plugin gives them its own environment without the variables that hold keys, tokens or service addresses, so no credential of the coordinator's reaches Pi or the commands it runs. From a coordinator on Windows, the copy inside WSL starts through `wsl.exe` with a `PATH` of its own folder and the system folders and without the user's shell profile, so tools installed per user (a Node version manager, say) are not found by its commands.
- Beside the key file, `config\keys-revision` holds a number that AI Meter raises twice at every change of the key file: to an odd number before it touches the file, to the next even one when the change is complete. A plugin reads the keys before and after a task to remove them from what it returns, and keeps the task's output only if the number was even at the start and is the same, or greater by two, at the end; anything else means that a key may have come and gone unseen, or that a change was under way, and the plugin withholds the output. A change that follows an unfinished one takes the next odd number, so two changes are never counted as one. Removing the copy counts as a change too, and `keys-revision` is the one file removal leaves behind, so the number never goes back to a value an earlier task has seen.
- Settings offers a key's switch only for a service whose list has a free model with tool support, which is what a delegated task runs on. The key file is brought in step with AI Meter's keys after every reading of the services, so a key changed in another app, where AI Meter only finds it, does not stay behind.
- With sample data on, AI Meter neither offers the copy nor touches its key file. A new installation starts with nothing ticked; replacing an older release keeps the choices.
- The copies start with `--no-skills` and `--no-prompt-templates` besides the options shared with Pi in WSL: Pi would otherwise also load skills from `~/.agents/skills`, outside its configuration folder, and name them to the model.
- Two sessions of one Windows account can each run AI Meter on the same key file. Before it reconciles the keys, each takes the ticked services as the other last saved them, so a key withdrawn in one session is not copied back by the other.
- An `environment_policy` that AI Meter does not know, in a definition or one of its places, makes `describe_dispatch` refuse the candidate: a misspelt policy never turns into inheriting the whole environment.
- AI Meter carries Pi's own model lists for the pinned release (`agents\pi-models`, from the `pi-ai` package, MIT) and writes them to `managed\pi\models\<version>`: the standalone build keeps them inside its program. They supply what a service's list leaves out, so through a copy an NVIDIA model whose tool support the service does not state is a best-effort candidate, and Cloudflare's free-tier models are offered with `include_free_tier`, since Pi prices their requests. For Cloudflare the key file also holds the account id (`env.CLOUDFLARE_ACCOUNT_ID`), and the token must allow Workers AI, not only Account Analytics.
- A ticked service keeps its key in each copy's file while AI Meter finds no key for it at all: a key discovered in another app is out of sight whenever that app's files are (WSL not running, the first moments after a start). A key found and different replaces it; removing the key in AI Meter unticks the service, and entering a key for it again ticks it again, unless the user set its switch meanwhile or removed the copies.
- **The agent can read the key file.** Pi's tools are not confined to the task's folder. Like any coding agent run as the user, the copy can read the user's files, including its own plain-text key file, so a model that is told to, or tricked into it, can see the keys copied there (all of them, not only the key of the service the task runs on). With both copies installed there are two such files, one on Windows and one in the Linux home. The plugin removes any of those keys from the answers it returns to the coordinator, but the model's service has then already received it. Copy only keys of services whose free-tier exposure you accept, and do not delegate tasks on content you do not trust. The same holds for the user's own Pi and its `~/.pi`.

**Listing it.** In `list_candidates` the copy is a runtime of its own, on candidates and in `runtimes.found`:

```json
"runtime": { "name": "pi", "where": "windows", "managed": true }
```

`runtimes.managed` is present whenever the build offers a copy: an array with one entry per place a copy can be installed in. The entry for Windows is always there; the one for WSL when a distribution is set, and while that distribution does not run it is reported `installed: false` and `reachable: false`, because AI Meter does not look inside it.

```json
"managed": [
  {
    "name": "pi", "where": "windows", "installed": true, "reachable": true, "version": "1.5.2", "services": ["deepseek", "openrouter"],
    "set_up_in": "AI Meter › Settings › AI apps › Delegated tasks",
    "note": "An optional copy of Pi that AI Meter downloads when the user asks, for tasks on Windows folders started from Windows. Only the user can install it or give it a key, in Settings: tell them, do not try to."
  },
  {
    "name": "pi", "where": "wsl", "distro": "Ubuntu-24.04", "installed": false, "reachable": true, "version": "1.5.2", "services": [],
    "set_up_in": "AI Meter › Settings › AI apps › Delegated tasks",
    "note": "An optional copy of Pi that AI Meter keeps inside WSL, for tasks started from Windows or from WSL, with a Linux shell. Only the user can install it or give it a key, in Settings, while WSL is running: tell them, do not try to."
  }
]
```

`services` are those the copy holds a key for, including a service it gives no candidate for because the user's own Pi in the same place can already call it. These keys are the user's explicit choice, so they count with *Use API keys already on the PC* turned off too.

`runtimes.environment` says what a plugin needs to name the next step, read from what AI Meter already knows (nothing is started):

```json
"environment": { "wsl": "running", "distro": "Ubuntu-24.04", "git_for_windows": true }
```

`wsl` is `running`, `stopped` or `none` (no distribution set); `distro` is absent when it is `none`.

**When it is offered.**
- The user's own agent wins, by two rules. A copy is not offered for a service that a usable Pi of the user's in the same place can already call with its own key, unless the user chose otherwise in Settings (setting `ManagedPiBesideOwn`). Today no Pi of the user's on Windows is verified, so the copy on Windows is offered whenever it is installed. And when a copy and another agent of the user's (OpenCode, say) can run the same model, the user's agent comes first, ahead of the fewer-tokens rule of the order under `list_candidates`. Each is named.
- Between the two copies, for the same model, the one in the preferred place comes first: inside WSL, unless the user chose Windows (Settings shows "From Windows, try first" when both are installed; setting `ManagedPiPrefer`). A plugin can still pin a copy with `where`.
- A coordinator in WSL (`from: "wsl"`) is offered the copy in its own distribution, never the copy on Windows; a coordinator on Windows is offered both. The copy on Windows works on Windows folders only: a `workspace` under `\wsl.localhost\` or `\wsl$\` answers `supported: false` for it, and the plugin also skips it when the job's repository is inside WSL, since `describe_dispatch` only sees the plugin's working copy. The copy inside WSL works on folders inside WSL, and on Windows folders through `/mnt/…`.
- Never for a subscription plan, which needs the vendor's own CLI and sign-in.
- The Microsoft Store build does not offer either copy for now (`OfferedInStoreBuild` is `false`): it will, in a later release, once the Store listing discloses the downloads ([STORE.md](STORE.md#optional-download-managed-pi)). Until then it keeps "use the agent you have".

**Choosing the shell (the copy on Windows).** Pi resolves its shell each time it runs a command, so AI Meter binds it in the copy's own settings instead of leaving it to the plugin. The copy inside WSL needs none of this: its bash tool runs the distribution's own bash.
- When AI Meter finds Git for Windows (at installation, at start, and whenever keys or choices change), it writes Pi's `shellPath` setting into `config\settings.json`. A binding once written is replaced when Git is found elsewhere and never taken back: should Git Bash disappear, the alternative is no longer offered, and a task already described with it fails at its first command instead of searching. With `shellPath` set, Pi 1.5.2 uses that file alone and fails if it is gone: it never searches `PATH`, so never WSL's `bash.exe` launcher, and a `.pi\settings.json` in the task's folder does not override it (Pi's `utils/shell.ts`; run live on 2026-11-20).
- The `arguments` of `describe_dispatch` need no preparation: in `modify` mode they give Pi its PowerShell tool and no bash (`commands: "powershell"`); in `read_only` mode Pi gets only reading tools (`commands: "none"`).
- `alternatives` lists the arguments for `git_bash` (`modify` only), and only while the copy's settings name a `bash.exe` that exists. The plugin uses the alternative only when it is offered and decides nothing by its own search. Without it the copy has the PowerShell tool only.

**Outcome and exit code.** As for Pi in WSL, with one difference: the standalone builds, Windows and Linux alike (the Linux one run live on 2026-11-24), exit with 1 when it cannot make a request at all (no key for the provider). There is then no `agent_settled` event and the reason is on stderr. Errors during a run still end with exit code 0 and a `stopReason`. The copies start with `PI_OFFLINE=1`, `PI_SKIP_VERSION_CHECK=1` and `PI_TELEMETRY=0`; once it runs a task it contacts the model's service like any agent. `PI_OFFLINE=1` keeps it from downloading anything during a task: Pi 1.5.2 would otherwise fetch ripgrep or fd from GitHub when its grep and find tools need them and they are not on `PATH`; with it those tools fail with an error instead (expected from Pi's source, not run live). Inside WSL they need ripgrep and fd installed in the distribution.

**Limits.**
- Pi's tools are not confined to the task's folder, and the agent can read the copy's key file: see *Its own configuration and keys* above.
- A request to a real model through either copy, a whole task through the plugin and the copy inside WSL, the Settings section in the running app and the Store-signed build starting a copy were not verified when this was written. The live checks used a local stand-in for the model, and on 2026-11-24 the Linux archive's digest, AI Meter's own installer putting the copy inside WSL (Ubuntu-24.04 under WSL 2, 7 s), the program printing 1.5.2, and a start from Windows through `wsl.exe` on a Windows folder (it works in `/mnt/c/…`); without a key for the provider it exits with code 1 and "No API key found for …" on stderr.
- One WSL distribution: the one AI Meter is set to.
- Tasks on Windows folders run from the copy inside WSL through `/mnt/c`: Git commands inside the plugin's working copy do not find the repository, access is slow on projects with many small files, and toolchains that run only on Windows do not run. The copy on Windows is there for those projects.
- From a coordinator on Windows the copy inside WSL has no user shell profile, so tools installed per user are not on its `PATH` (see above).
- Stopping a task with `taskkill /T /F` ends Pi and its shells, but a command a shell started can outlive them (issue [#37](https://github.com/dichiara19/ai-meter/issues/37)).
- Both are x64 builds.

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
| `request_tokens` | About how many tokens the agent sends with every request on top of the task (its system prompt and tools), as started for delegation. For the same model, the agent with fewer comes first among candidates; unstated counts after stated |
| `programs` | Program names per place (`windows`, `wsl`), without an extension. On Windows `.exe`, `.cmd`, `.bat` and `.ps1` are tried, in that order |
| `folders` | Settings folders relative to the home: any one shows the agent is set up |
| `keys` | Where the agent keeps API keys: a home-relative `file` and a JSON `path`, with `{id}` for the service's aliases (`auth.ids` in its service definition), or a `file` with `"format": "env"`, a dotenv file of `NAME=value` lines read by the variable names in the service's `auth.env` (Hermes keeps its keys this way) |
| `package` | npm package, to read the version without running the agent; an agent npm installed in WSL also needs `node` |
| `models` | The agent's own model list, for [best-effort candidates](#best-effort-candidates) and a plan's models: `format` (`pi`, `models.dev` or `codex`), `file` under `{home}/` or `{package}/` with `{provider}` for the service's alias, and optional `versions` whose layout was checked. Pi: `{package}/node_modules/@earendil-works/pi-ai/dist/providers/data/{provider}.json` for 1.x; OpenCode: `{home}/.cache/opencode/models.json`; Codex: `{home}/.codex/models_cache.json`, of which only each model's slug, name, visibility, priority and context window are read |
| `program_folders` | Further home-relative folders per place where the program may be, searched after the usual ones; `*` stands for each subfolder, most recently written first (Codex's app keeps `codex.exe` in `AppData/Local/OpenAI/Codex/bin/<build>`) |
| `plan` | For an agent that runs on a subscription plan with its own sign-in ([Subscription plans](#subscription-plans)): `service` (whose windows AI Meter reads), optional `models`, most suitable first: ids, or objects with `id` and the `context_tokens` and `max_output_tokens` a source states (otherwise the agent's `models` list), and `sign_in`: the `arguments` of its status command, with `json_field` and `expect`, or `text`, that show a plan sign-in. A plan without a sign-in check is ignored |
| `dispatch` | How a plugin starts it. Without it, the agent is reported in `runtimes.unsupported` |
| `dispatch.environment_policy` | `clean`: the plugin starts the agent from an empty environment plus `environment`, never its own (plan agents). A place may state `no_credentials` instead (the managed Pi, both copies): the plugin's own environment without the variables that hold keys, tokens or service addresses, so the agent keeps `PATH` and the user's tools but none of the coordinator's credentials |
| `dispatch.places` | The verified places, each with `versions` (`"1.x"` matches every 1.* version, `"1.4.0"` only that one), optional `executables` (native Windows programs inside the npm package) and optional own `arguments` and `environment`. Besides `windows` and `wsl`, the user's own installations, a place may be `managed` (the copy AI Meter keeps on Windows) or `managed_wsl` (the copy it keeps inside WSL; see below) |
| `dispatch.arguments` | The argument list per mode, `read_only` and `modify`. `read_only` must be enforced by what the agent is given, such as Pi's `--tools` list or OpenCode's per-agent `permission` in `OPENCODE_CONFIG_CONTENT` (see `agents/opencode.json`), never by an agent's name and defaults alone |
| `dispatch.environment` | Variables to set. A value written as a JSON object is serialised after its placeholders are filled, so a model id cannot change its structure |
| `dispatch.output`, `dispatch.notes` | Returned as they are in `describe_dispatch` |

The `managed` and `managed_wsl` places ([Managed Pi](#managed-pi); `agents/pi.json` is the only built-in definition with them) are not installations the user made, so AI Meter does not look for them: each counts when the user installed that copy. They hold the same fields; for `managed_wsl`, `commands` is `none` in `read_only` and `bash` in `modify`, with no `alternatives`, and its notes state the limits of tasks on Windows folders. Their own `output` and `notes` are added to the shared ones, and it may hold:

| Field | Meaning |
|---|---|
| `versions` | The exact release AI Meter downloads, such as `["1.5.2"]`; both places carry the same one |
| `providers` | The provider ids the standalone build has built in. A service can have its key copied into the copy only if one of its aliases (`auth.ids`) is listed |
| `arguments` | The argument list per mode, safe without preparation. For Pi in `modify`, `managed` gives the PowerShell tool and no bash, `managed_wsl` the bash tool |
| `alternatives` | Argument lists by shell name, then mode (`git_bash.modify`), that a plugin may use instead. `describe_dispatch` returns them as `[{ "shell", "arguments" }]`. For the managed copy only a shell it is bound to is returned (`git_bash`, while its settings name an existing `bash.exe`) |
| `commands` | Per mode, what the agent runs commands with when started with `arguments`: `none`, `bash` or `powershell` |
| `environment` | As for other places; `{config}` stands for the copy's own configuration folder, as in `"PI_CODING_AGENT_DIR": "{config}"` |
| `output`, `notes` | `output` replaces the shared `dispatch.output` by key (Pi's exit code is 1 when it cannot make a request at all); `notes` come after the shared `dispatch.notes` |

Placeholders, filled in one pass: `{provider}` (the alias the agent keeps the key under), `{model}`, `{workspace}` (`.` inside WSL when started from Windows, where `wsl.exe --cd` already sets the folder) and, in the `managed` place only, `{config}`. Agents without a verified dispatch are still useful: their folders and programs show them in `runtimes.unsupported`, and their `keys` let AI Meter find keys for the services it meters.

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
- For the same reason the Store package cannot create the configuration file of Claude Desktop or VS Code: open it once in the app (Claude Desktop: Settings → Developer → Edit Config; VS Code: *MCP: Open User Configuration*), then connect. A file that exists is edited as in the other builds.
