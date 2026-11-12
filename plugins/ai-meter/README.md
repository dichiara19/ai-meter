# AI Meter delegation plugin

A plugin for **Claude Code** and **Codex** that hands self-contained coding tasks to a coding agent you already have, such as Pi or OpenCode, on a free model, or, when you allow it, to Codex or Claude Code on your own subscription plan. [AI Meter](../../README.md) chooses where the task can run and paces it; the plugin runs the agent ([ADR 0002](../../docs/adr/0002-delegation-through-coordinator-plugins.md), [ADR 0003](../../docs/adr/0003-delegation-on-subscription-plans.md)).

The same folder is both plugins, each in its coordinator's standard layout:
- **Claude Code** reads `.claude-plugin/plugin.json` and `.mcp.json`.
- **Codex** reads the portable Agent Plugins 1.0.0 manifest: `plugin.json`, with its settings under `extensions.com.openai`, and `mcp.json`, where the server is declared as `stdio`.

Both get the same MCP server and the same `delegate` skill. The marketplaces are at the repository root, `.claude-plugin/marketplace.json` and `.agents/plugins/marketplace.json`. The plugin's version is kept in its manifests only.

## What you need

- **AI Meter 0.10.0 or later on Windows**, from the Microsoft Store or from GitHub: it has the delegation tools (`list_candidates`, `describe_dispatch` and the capacity leases). The plugin reaches AI Meter through the relay AI Meter writes, which starts AI Meter when it is not running: `%APPDATA%\AIMeter\mcp.ps1` for the GitHub copy, `~\.aimeter\mcp.ps1` for the Store copy. When both exist, for example after moving from one copy to the other, the plugin uses the one written last, since each copy rewrites its own at every start.
- **Node.js 18 or later** where the coordinator runs: on Windows, or in WSL.
- **At least one coding agent set up with its own key** for a service with free models, among those AI Meter can start (its [agent definitions](../../docs/MCP.md#agent-definitions); today Pi in WSL, and OpenCode on Windows or in WSL). The plugin never installs an agent and never handles a key. Set it up yourself, for example with `opencode auth login`.
- **For tasks on a plan** (optional): Claude Code or Codex in WSL, signed in with your Claude or ChatGPT plan (`claude` then `/login`, or `codex login`), and the plan turned on in AI Meter's Settings › AI apps › Delegated tasks.
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
| `delegate` | Starts a background job and returns its id. Takes `task`, `workspace` (absolute path), `mode`, and optionally `service`, `model`, `runtime`, `where`, `distro`, `min_context`, `allow_best_effort`, `allow_free_tier`, `allow_plan`, `coordinator`, `cost_policy`, `timeout_seconds` (default 1200, at most 7000) and `wait_seconds` (at most 50) |
| `delegate_result` | The job's report: status and `duration_seconds`, the agent's answer, what the agent did (`activity`), requests, tokens and price, the change made by a `modify` task, AI Meter's `warnings`, and the candidates that were skipped and why (see *The report*) |
| `delegate_status` | The latest jobs |
| `delegate_cancel` | Stops the agent and releases the lease |
| `delegate_candidates` | Where a task could run now, from AI Meter's `list_candidates`, with `runtimes.unsupported`: the agents found that cannot be used, each with a sentence saying why. With `include_best_effort`, also the best-effort candidates; with `include_free_tier`, also the free-tier ones; with `include_plan`, also Claude Code or Codex on your plans, each with `billing` and its `plan` windows. A warning every candidate of a pool carries is given once, on that pool in `pools`; `excluded` lists model names only for reasons the coordinator can act on (`free_tier_not_allowed`, `runtime_has_no_key`, `context_too_small`, `output_too_small`, `agent_lists_price`, `plan_not_requested`, `plan_off_in_settings`, `plan_reserve`), and only the count for the others. Starts nothing |

**How a job runs.**
1. `list_candidates` with `tools: true`, narrowed by the pins you gave.
2. For each candidate in AI Meter's order: `describe_dispatch` for this place (`from` is `windows` or `wsl`, with the candidate's WSL `distro`), then `claim_capacity`.
   - A candidate is skipped, with the reason, when AI Meter cannot describe it here (`supported: false`, or an error such as "no key of its own" or a key only exported in a WSL shell profile when the plugin runs on Windows), when its pool has no room, when the plugin cannot read that agent's events, or, for a plugin in WSL, when the agent is in another distribution.
   - AI Meter leaves out a free model whose agent prices it in its own model list (`agent_lists_price`): the agent would pay for the first request before the plugin could stop it.
   - The plugin reads the events of Pi and OpenCode. An agent added to AI Meter's definitions later needs a reader here too.
   - The plugin describes before claiming, so it holds a lease only for an agent it can start. "Still reading the first data" is waited for, up to a minute.
3. The agent starts exactly as described: the program and its argument list, without a shell. The task is written to stdin, then stdin is closed.
4. The agent's JSONL events give the outcome, the requests, the tokens, the price of each request and any `429`. Pi's exit code stays 0 on errors, so its outcome comes from `stopReason`. A task is `completed` only on the agent's own normal end: Pi's `stopReason: "stop"` after `agent_settled`, OpenCode's last `step_finish` with reason `stop` and exit code 0. An answer cut off for length, or output without those events, fails with the reason. Tokens no event reported are `null`, not zero.
5. **The first request priced above zero stops the agent**, and the task fails with `paid_request: true`. That request was already made and is billed. A price of zero does not prove a request was free (see *Free is not always free*).
6. `release_capacity` with the outcome and the counters. A stopped task (cancelled, timed out or paid) reports no tokens, because the agents do not report them when stopped.

A job runs in its own worker process, so it outlives the call that started it.
- **Timeout:** `timeout_seconds` covers the whole job, from choosing a candidate to the agent's end. Each request to AI Meter also has its own deadline (30 s; 90 s for the first, which may start AI Meter), so a relay that hangs cannot hold a job in `starting`, nor its end.
- **Cancelling** stops whatever is in progress. The agent is stopped with its children: `taskkill /T /F` on Windows, which also ends an agent started in WSL through `wsl.exe`; elsewhere `SIGINT` to the agent's own process group, then `SIGKILL`. On Linux the group is also ended when the agent exits. On Windows, processes an agent leaves behind after exiting on its own are not tracked.
- **A worker that dies** leaves its job to the next `delegate_result` or `delegate_cancel` for it, which stops the agent, removes the snapshot, releases the lease and saves the end.
- **Limits:** at most 4 jobs at once; event lines up to 4 MiB (longer ones are dropped and counted in `dropped_lines`); 64 MiB of events per job, past which the agent is stopped; 1 MiB of error output kept. The plugin's MCP server refuses request lines over 1 MiB and malformed requests with a JSON-RPC error, and goes on.

**The report** (`delegate_result`, and `delegate` when the job ends within `wait_seconds`) is meant to let the coordinator judge the result without opening the logs.
- `status`, `error` (only when the job did not complete) and `duration_seconds`, from the job's creation to its end.
- `answer`: the agent's last message, at most 20,000 characters. It is the agent's own claim.
- `activity`: what the agent did, read from its tool events (Pi's `tool_execution_start` and `tool_execution_end`, OpenCode's `tool_use`), or absent when it made no tool call. It is the evidence to check the answer against.
  - `tool_calls` and `failed_calls`: how many calls, and how many the agent reported as failed;
  - `calls`: each call with its `tool`, `args` (a one-line summary: the command, the path or the pattern), `failed` and, for a failed call other than a command, a short `error`; `unfinished` when the call never ended;
  - `files_written`: the files written or edited by calls that did not fail;
  - `commands`: the commands run, with `ok`, `exit_code` when the agent reports it, and `output_tail`, the last 300 characters of the output, where test results and errors usually are.

  Paths are relative to the task's folder, which shows as `.`. Only the last 30 calls and 10 commands are kept (`earlier_calls_left_out`, `earlier_commands_left_out`) and 50 file paths (`more_files_left_out`). `activity` never holds file contents, and holds a command's output only as that excerpt. A failed call is not a failed task: agents often try a command that does not match and go on.
- `plan`, for a task on a subscription plan: the plan, the window it drew on, and the room before and after it (see *Your subscription plans*).
- `counters`: `outcome`, `requests` (`null` for Codex, which does not report them), `rate_limited`, tokens, `cost`, `paid_request`, `byok_fallback`, `billed` for a plan agent stopped for billed usage, and `error`. On a completed task, errors the agent recovered from, such as a `429` that Pi retried, are in `recovered_errors`: their `count` and the `last` one, shortened to the provider's message. They are still counted in `rate_limited`.
- `worktree` for a `modify` task (see below), `warnings`, `skipped`, `notes` from AI Meter's dispatch description, and `logs`: the paths of the full event log and error output.

**The agent never works in your folder.** The workspace must be in a Git repository.
- **Read-only by default.** Pi gets only its reading tools. OpenCode gets its `plan` agent with permissions set by the agent definition, not left to its defaults or your config: every tool but reading, searching, LSP and the todo list is denied, and so is reading `.env` files. The agent works on a copy of the files Git tracks or would track, as they are on disk. Ignored files, symbolic links, submodules and files whose names suggest secrets (`.env` and its variants except `.env.example`, `.npmrc`, `.pypirc`, `.netrc`, `*.pem`, `*.key`, `*.p12`, `*.pfx`, private SSH keys) are left out, and the report lists the secrets it left out. The copy is in `~/.aimeter/snapshots` and is removed when the task ends.
- **`modify`** runs the agent in a new Git worktree on branch `ai-meter/<job id>`, made from the last commit, so ignored files such as `.env` are not in it. Uncommitted changes in your checkout are not in it either, and the report says so; it also lists committed files that look like secrets. The report compares with that base commit, so it shows commits the agent made (`worktree.commits`) as well as uncommitted and new files. New files are marked intent-to-add (`git add -N`) in the worktree's index first, so they are in `files` with status `A`, in `diff_stat` and in the output of the `review` command; no content is staged. OpenCode's `build` agent may not read `.env` files or folders outside the worktree. The plugin never commits, merges or removes the worktree: the report gives the commands to review and to discard it.
- **Neither is a sandbox.** An agent can still read a path outside its folder if it is given one, Pi in particular, which has no permission rules.

**Choosing a model and an agent.** The plugin takes the first candidate it can start, in AI Meter's [order](../../docs/MCP.md#order):
1. models you mark *Preferred* in AI Meter's Settings › AI apps › Delegated tasks, and *Avoided* ones last;
2. pools with at least 20% of their room left, or unknown room;
3. each model's track record on this PC over 30 days: completed tasks minus failed and rate-limited ones;
4. a reply size of at least 16,384 tokens;
5. the model's total weights (from Hugging Face or its id; unknown sizes first), then its context window, then the speed measured here.

For the same model, the agent with the smaller fixed cost per request comes first (Pi sends about 4,600 tokens of system prompt per request, OpenCode, isolated, about 9,600), then the agent in the plugin's own place, which starts without `wsl.exe`. `delegate_candidates` gives each candidate's `context_tokens`, `max_output_tokens` and `why`, the reason for its place. No catalogue states quality: when it matters, pin `model`, `runtime` or `where`.

**Best effort, only with consent.** NVIDIA and the Gemini API do not state prices or tool support, so their models are never ordinary candidates. AI Meter can fill in what is missing from the agent's own model list ([best-effort candidates](../../docs/MCP.md#best-effort-candidates)).
- With `allow_best_effort: true`, the plugin tries these models after every ordinary candidate.
- The report marks the choice with `candidate.best_effort` and `estimated`.
- The skill tells the coordinator to ask the user first, and not to retry a failed model on its own.
- Live on 2026-11-02, with real keys:
  - 20 NVIDIA models and 2 Gemma models on the Gemini API became best effort;
  - 21 Gemini models stayed out as `agent_lists_price`, because Pi prices them at the paid-tier rate.

**Free tiers, only with consent.** Cloudflare Workers AI prices every model in Neurons, and only the first 10,000 Neurons of each UTC day are free ([free tiers](../../docs/MCP.md#free-tiers-cloudflare-workers-ai)).
- With `allow_free_tier: true`, the plugin also uses these models. AI Meter offers them only through an agent whose own model list prices them, so the plugin sees what the task spends.
- The lease carries a budget: what is left today beyond a reserve of 1,000 Neurons, in US dollars (`budget.max_cost`). The plugin stops the agent once its requests cost more, and the task fails. A lease without a budget is given back and the candidate skipped.
- The report shows `budget`, and `counters.cost` with `counters.allowance`. The cost goes back to AI Meter with the lease, so the next task's budget counts it before Cloudflare's analytics do.
- One request can still cross the budget before the plugin stops the agent. On the Workers Free plan Cloudflare refuses requests past the allocation; on the Paid plan that request is billed, at $0.011 per 1,000 Neurons.

**Free is not always free.** OpenRouter can retry a `:free` request that hit the free route's limit on a provider key you linked to OpenRouter (BYOK), billed to that provider account. AI Meter flags OpenRouter candidates with `warnings: ["openrouter_byok_fallback"]` (in `delegate_candidates`, once on the `openrouter:free` pool), and the report repeats it.
- The agents price requests from their own model lists, so a BYOK fallback shows a price of zero.
- When OpenRouter's error names the linked key (`"is_byok":true`), the report sets `byok_fallback: true`. This was seen live on 2026-11-02: a `:free` request fell back to a provider key linked to OpenRouter, whose provider refused it with 402.
- A fallback that succeeds is not visible to the plugin. To rule it out, unlink BYOK keys in OpenRouter before delegating.

**Your subscription plans, only with consent, never pay-as-you-go.** A Claude or ChatGPT plan often resets with room left. With `allow_plan: true`, a coordinator in Claude Code can hand a task to **Codex on your ChatGPT plan**, and one in Codex to **Claude Code on your Claude plan** ([subscription plans](../../docs/MCP.md#subscription-plans), [ADR 0003](../../docs/adr/0003-delegation-on-subscription-plans.md)).
- **Two consents.** Turn the plan on in AI Meter's Settings › AI apps › Delegated tasks (off by default), and the coordinator asks you before passing `allow_plan`.
- **Never the coordinator's own plan.** The plugin tells AI Meter which service the coordinator runs on: Claude Code is `claude`, Codex is `openai`, from the client name; pass `coordinator` to say otherwise. That plan is excluded (`same_plan_as_coordinator`). Two accounts of one service count as the same plan.
- **Your reserve.** A plan is used only while every window has more room than you keep for yourself: by default 40% of the 5-hour window and 25% of the weekly one, set in the same place. A reading older than 15 minutes, or none, means no plan task.
- **Order.** Free models come first and the plan is the fallback, except when its room is about to be lost: a 5-hour window resetting within the hour, or a weekly one within a day.
- **The pay-as-you-go guard**, each layer enough on its own for what it covers:
  - AI Meter leaves a plan out while Claude's extra usage is on, or Codex has credits it would spend past the plan, and when it cannot see a plan sign-in;
  - the agent starts from a clean environment: only the system folders, the home and a PATH. No API key, base URL or session variable of the coordinator reaches it (`scripts/lib/plan.mjs`);
  - Claude Code runs with `--safe-mode --restricted` (no settings files, so no `apiKeyHelper`; no user skills, plugins, hooks or MCP servers), Codex with `--ignore-user-config` and `forced_login_method="chatgpt"`, without your skills, plugins, apps, hooks, memories or `AGENTS.md`;
  - before the lease, the plugin runs the agent's own `claude auth status` or `codex login status` in that environment: anything but a plan sign-in skips the candidate. Only the sign-in method is read; the account it names is dropped;
  - while it runs, Claude Code is stopped if its events show an API key (`apiKeySource`) or extra usage in use (`isUsingOverage`), and the task fails with `counters.billed`.
- **The report** names the plan and the window the task drew on: `plan.window`, `plan.before` (each window's room, your reserve and the reset when the lease was taken) and `plan.after` (Claude Code's own report of the windows, or AI Meter's fresh reading for Codex). The room after includes anything else your account used meanwhile, so the difference is not this task's alone. Tokens are counted; no saving in money is claimed.
- **Where.** Verified in WSL on 2026-11-12 with Claude Code 2.1.322 and Codex 0.163.0. On Windows, Codex's read-only sandbox refused to read the workspace and Claude Code had no sign-in to check, so neither runs there yet.
- **Each vendor's terms of use apply to its CLI.** The plugin uses only the interfaces Anthropic and OpenAI document for scripted use (`claude -p`, `codex exec`), with your own sign-in, one task at a time per plan.

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

- **The task and every file the agent reads go to the provider of the chosen model.** Providers of free models may log prompts or train on them. See above for what the agent can see. Never put secrets in a task.
- Copies of the task text, the agent's events and its error output are kept on this PC, in `~/.aimeter/jobs/<job id>`, on the side where the coordinator runs (Windows or WSL). Folders of ended jobs are removed after seven days, when another task starts.
- Worktrees of `modify` tasks are in `~/.aimeter/worktrees` and are never removed by the plugin.
- AI Meter's lease book holds only pool, model, agent, times and counters ([README](../../README.md#privacy-policy)).
- OpenRouter can route a `:free` model to a provider key you linked to your OpenRouter account (BYOK), billed to that account.
- A task on your plan goes to Anthropic or OpenAI under your own account and their terms, as your own use of Claude Code or Codex does. Before starting one, the plugin runs the agent's `auth status` or `login status` and keeps only the sign-in method, never the account.

## Limits

- AI Meter describes only the agents and places its definitions mark as verified, today Pi in WSL, OpenCode in WSL and on Windows, and Claude Code and Codex on a plan in WSL. A plugin in WSL cannot start a Windows agent, nor one in another distribution, yet. Other agents found are listed in `runtimes.unsupported` with the reason.
- The reserve binds only tasks run through AI Meter: your own sessions on the same plan go on as before.
- **Not verified live:** that `taskkill /T` on `wsl.exe` ends every Linux process an agent started inside WSL; and OpenCode's permission rules against a real OpenCode 1.x.
- Long tasks hold one lease for their whole timeout plus two minutes, rather than renewing it.
- A coordinator inside a packaged app, such as the Claude desktop app, may not see agents installed elsewhere. The plugin uses the paths AI Meter describes and installs nothing.
- **Not verified yet:** a task started by Codex's own model (its usage limit was reached during the check); Claude Code and Codex running natively on Windows (neither CLI is installed on the test PC; the MCP server itself was checked natively on Windows); the interactive screens.

## Development

```bash
node --test plugins/ai-meter/tests/delegate.test.mjs plugins/ai-meter/tests/plan.test.mjs
```

The tests use a fake AI Meter (`tests/fixtures/fake-meter.mjs`, through `AI_METER_RELAY`) and fake Pi and OpenCode agents. Claude Code and Codex replay runs recorded live on a plan (`tests/fixtures/claude-plan.jsonl`, `codex-plan.jsonl`, trimmed, with no personal data). `AI_METER_JOBS` moves the jobs folder. `scripts/delegate.mjs` offers the same operations from a terminal (`start`, `result`, `status`, `cancel`, `candidates`).
