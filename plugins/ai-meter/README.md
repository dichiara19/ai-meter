# AI Meter delegation plugin

A plugin for **Claude Code** and **Codex** that hands self-contained coding tasks to a coding agent you already have, such as Pi or OpenCode, on a free model. [AI Meter](../../README.md) chooses where the task can run and paces it; the plugin runs the agent ([ADR 0002](../../docs/adr/0002-delegation-through-coordinator-plugins.md)).

The same folder is both plugins, each in its coordinator's standard layout:
- **Claude Code** reads `.claude-plugin/plugin.json` and `.mcp.json`.
- **Codex** reads the portable Agent Plugins 1.0.0 manifest: `plugin.json`, with its settings under `extensions.com.openai`, and `mcp.json`, where the server is declared as `stdio`.

Both get the same MCP server and the same `delegate` skill. The marketplaces are at the repository root, `.claude-plugin/marketplace.json` and `.agents/plugins/marketplace.json`. The plugin's version is kept in its manifests only.

## What you need

- **AI Meter on Windows**, with the delegation tools (`list_candidates`, `describe_dispatch` and the capacity leases). They are not in a release yet: until M6, build AI Meter from `main` (`./build.ps1`). The plugin also works with the first version of these tools, but the agent definitions ([PR #30](https://github.com/dichiara19/ai-meter/pull/30)) add the WSL distribution, `runtimes.unsupported`, `warnings` and the request prices it uses. The plugin reaches AI Meter through the relay AI Meter writes (`%APPDATA%\AIMeter\mcp.ps1`), which starts AI Meter when it is not running.
- **Node.js 18 or later** where the coordinator runs: on Windows, or in WSL.
- **At least one coding agent set up with its own key** for a service with free models, among those AI Meter can start (its [agent definitions](../../docs/MCP.md#agent-definitions); today Pi in WSL, and OpenCode on Windows or in WSL). The plugin never installs an agent and never handles a key. Set it up yourself, for example with `opencode auth login`.
- Git, for tasks that change files.

## Install

**Claude Code** (2.1.292 or later, in one step):

```bash
claude plugin install ai-meter --marketplace dichiara19/ai-meter
```

Claude Code asks you to trust the marketplace first, as it does for every marketplace that Anthropic does not run. Older versions need the two steps: `claude plugin marketplace add dichiara19/ai-meter`, then `claude plugin install ai-meter@ai-meter`.

**Codex:**

```bash
codex plugin marketplace add dichiara19/ai-meter
```

```bash
codex plugin add ai-meter@ai-meter
```

Codex copies the plugin into `~/.codex/plugins/cache`; `codex plugin marketplace upgrade` updates it. A local checkout also works as the marketplace: pass its folder instead of `dichiara19/ai-meter`.

## Use

Ask in your own words, for example "delegate to a free model: read `src/Leases.cs` and list what each public method does". In Claude Code the skill is also `/ai-meter:delegate`.

The coordinator calls these tools of the `ai-meter-delegate` server:

| Tool | What it does |
|---|---|
| `delegate` | Starts a background job and returns its id. Takes `task`, `workspace` (absolute path), `mode`, and optionally `service`, `model`, `runtime`, `where`, `distro`, `min_context`, `allow_best_effort`, `cost_policy`, `timeout_seconds` (default 1200, at most 7000) and `wait_seconds` (at most 50) |
| `delegate_result` | The job's report: status, the agent's answer, requests, tokens and price, the changed files of a `modify` task, AI Meter's `warnings`, and the candidates that were skipped and why |
| `delegate_status` | The latest jobs |
| `delegate_cancel` | Stops the agent and releases the lease |
| `delegate_candidates` | Where a task could run now, from AI Meter's `list_candidates`, with `runtimes.unsupported`: the agents found that cannot be used, each with a sentence saying why. With `include_best_effort`, also the best-effort candidates. Starts nothing |

**How a job runs.**
1. `list_candidates` with `tools: true`, narrowed by the pins you gave.
2. For each candidate in AI Meter's order: `describe_dispatch` for this place (`from` is `windows` or `wsl`, with the candidate's WSL `distro`), then `claim_capacity`.
   - A candidate is skipped, with the reason, when AI Meter cannot describe it here (`supported: false`, or an error such as "no key of its own"), when its pool has no room, or when the plugin cannot read that agent's events.
   - The plugin reads the events of Pi and OpenCode. An agent added to AI Meter's definitions later needs a reader here too.
   - The plugin describes before claiming, so it holds a lease only for an agent it can start. "Still reading the first data" is waited for, up to a minute.
3. The agent starts exactly as described: the program and its argument list, without a shell. The task is written to stdin, then stdin is closed.
4. The agent's JSONL events give the outcome, the requests, the tokens, the price of each request and any `429`. Pi's exit code stays 0 on errors, so its outcome comes from `stopReason`.
5. **The first request priced above zero stops the agent**, and the task fails with `paid_request: true`. A price of zero does not prove a request was free (see *Free is not always free*).
6. `release_capacity` with the outcome and the counters. A stopped task (cancelled, timed out or paid) reports no tokens, because the agents do not report them when stopped.

A job runs in its own worker process, so it outlives the call that started it. Cancelling stops the agent with `taskkill /T /F` on Windows, which also ends an agent started in WSL, and with `SIGINT` elsewhere.

**Read-only by default.** Pi gets only its reading tools, and OpenCode its `plan` agent. **`modify`** runs the agent in a new Git worktree on branch `ai-meter/<job id>`, made from the last commit. Uncommitted changes in your checkout are not in it, and the report says so. The plugin never commits, merges or removes the worktree: the report gives the commands to review and to discard it.

**Choosing a model and an agent.** AI Meter orders candidates by the room left in their pool, not by quality. Within one pool the order is by service, model, agent id and place, alphabetically. So when Pi and OpenCode both have a key, OpenCode comes first, and on Windows before WSL. When it matters, look at `delegate_candidates` and pin `model`, `runtime` or `where`. Pi sends about 4,600 tokens of system prompt per request, and OpenCode, isolated, about 9,600.

**Best effort, only with consent.** NVIDIA and the Gemini API do not state prices or tool support, so their models are never ordinary candidates. AI Meter can fill in what is missing from the agent's own model list ([best-effort candidates](../../docs/MCP.md#best-effort-candidates)).
- With `allow_best_effort: true`, the plugin tries these models after every ordinary candidate.
- The report marks the choice with `candidate.best_effort` and `estimated`.
- The skill tells the coordinator to ask the user first, and not to retry a failed model on its own.
- Live on 2026-11-02, with real keys:
  - 20 NVIDIA models and 2 Gemma models on the Gemini API became best effort;
  - 21 Gemini models stayed out as `agent_lists_price`, because Pi prices them at the paid-tier rate.

**Free is not always free.** OpenRouter can retry a `:free` request that hit the free route's limit on a provider key you linked to OpenRouter (BYOK), billed to that provider account. AI Meter flags OpenRouter candidates with `warnings: ["openrouter_byok_fallback"]`, and the report repeats it.
- The agents price requests from their own model lists, so a BYOK fallback shows a price of zero.
- When OpenRouter's error names the linked key (`"is_byok":true`), the report sets `byok_fallback: true`. This was seen live on 2026-11-02: a `:free` request fell back to a provider key linked to OpenRouter, whose provider refused it with 402.
- A fallback that succeeds is not visible to the plugin. To rule it out, unlink BYOK keys in OpenRouter before delegating.

## What the coordinators show

- **Claude Code.** The skill loads as `ai-meter:delegate`; each tool call appears as an MCP call to `ai-meter-delegate` with its arguments, and its JSON result. A job in the background is not shown until the coordinator asks for it with `delegate_status` or `delegate_result`.
- **Codex.** `codex mcp list` shows `ai-meter-delegate` from the plugin's cache folder. Tool calls appear as MCP calls in the session.

Checked with Claude Code 2.1.316 run headless (`claude -p`) and with Codex 0.163.0's plugin and MCP listings. The interactive screens of both were not observed yet.

## Does it save?

Measured on 2026-11-02 with Claude Code 2.1.316 on Sonnet 5.5 as coordinator, in WSL, against the same task done by Claude Code directly. There is no intermediary subagent: the coordinator calls the tools itself.

| Task | Directly | Delegated: coordinator | Delegated: agent (free) |
|---|---|---|---|
| Short: one file, a one-sentence answer | $0.217, 2 turns, 7 s | $0.301, 5 turns, 23 s; it also checked the answer in the file | OpenCode in WSL on `cohere/north-mini-code:free`: 2 requests, 17,646 input and 398 output tokens |
| Medium: three files of 270 to 900 lines, nine bullet points | $0.370, 4 turns, 29 s | $0.255, 5 turns, 42 s; asked to check lightly | Same agent and model: 2 requests, 37,996 input and 1,287 output tokens |

- **Short tasks cost more delegated.** The tool round trips, the skill and the check outweigh the file the coordinator does not read.
- **Longer reading saves coordinator tokens** (31% here), **at a cost in quality.** The free model's medium answer was shallower and partly wrong. It called the MCP server read-only and named a private method as public. The coordinator flagged these as unchecked.
- **These are single runs.** The first run of each pair also created the prompt cache, which the second reused: the delegated run went first for the short task, and second for the medium one. The medium comparison therefore leans towards delegation.

## Privacy

- The task text, the agent's events and its error output stay on this PC, in `~/.aimeter/jobs/<job id>`, on the side where the coordinator runs (Windows or WSL). Folders of ended jobs are removed after seven days. Never put secrets in a task.
- Worktrees of `modify` tasks are in `~/.aimeter/worktrees` and are never removed by the plugin.
- AI Meter's lease book holds only pool, model, agent, times and counters ([README](../../README.md#privacy-policy)).
- OpenRouter can route a `:free` model to a provider key you linked to your OpenRouter account (BYOK), billed to that account.

## Limits

- AI Meter describes only the agents and places its definitions mark as verified, today Pi in WSL, and OpenCode in WSL and on Windows. A plugin in WSL cannot start a Windows agent yet. Other agents found, such as Codex or Claude Code, are listed in `runtimes.unsupported` with the reason.
- Long tasks hold one lease for their whole timeout plus two minutes, rather than renewing it.
- A coordinator inside a packaged app, such as the Claude desktop app, may not see agents installed elsewhere. The plugin uses the paths AI Meter describes and installs nothing.
- **Not verified yet:** a task started by Codex's own model (its usage limit was reached during the check); Claude Code and Codex running natively on Windows (neither CLI is installed on the test PC; the MCP server itself was checked natively on Windows); the interactive screens.

## Development

```bash
node --test plugins/ai-meter/tests/delegate.test.mjs
```

The tests use a fake AI Meter (`tests/fixtures/fake-meter.mjs`, through `AI_METER_RELAY`) and fake Pi and OpenCode agents. `AI_METER_JOBS` moves the jobs folder. `scripts/delegate.mjs` offers the same operations from a terminal (`start`, `result`, `status`, `cancel`, `candidates`).
