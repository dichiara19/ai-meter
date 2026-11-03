# Orchestration contracts (draft)

> **Superseded where implemented.** The authoritative contract for every tool that exists is [docs/MCP.md](../MCP.md); this draft remains for the reasoning behind it and for what is not built yet.

Proposed contracts for milestones M1–M4 of the [agent orchestration plan](agent-orchestration.md). Every name below is a proposal. Each milestone finalises its part in code and in [docs/MCP.md](../MCP.md), and this file then points there.

The existing conventions of the MCP server stay:
- `snake_case` fields;
- local ISO 8601 times;
- percentages from 0 to 100;
- each result returned as `structuredContent` and as the same JSON in a text block.

New fields are additive. Any change in meaning is listed in the release notes.

## 1. Freshness of read tools (M1)

M1 implements this section. The reference is [docs/MCP.md, *Fresh data*](../MCP.md#fresh-data). This section keeps only the decisions and how the implementation differs from the first draft.

**Decisions:**
- **No refresh by default** (maintainer decision, 2026-11-02). Tools answer from the latest reading unless the caller passes `refresh` or `max_age_seconds`. This keeps the 0.8.6 promise.
- **No second collector.** Requests go through `RefreshGate` (`src/Freshness.cs`) to the existing collection worker. As a result:
  - concurrent callers share one collection, and a caller that joins a read already in progress starts no new one;
  - a caller that gives up cancels nothing;
  - periodic and requested collection never run side by side.
- **Separate timers.** Usage and free-model lists are invalidated separately: `InvalidateNetwork` for usage, `InvalidateCatalogue` for the list.
- **Upstream backoff** (`NetworkBackoff`). After a `429`, or a `503` with `Retry-After`, a service is not contacted before the time it asked for. Without `Retry-After`, the wait is 5 minutes. This applies to periodic reads too.
- **Minimum interval.** A service is asked on request at most once every 60 seconds.
- **Timeout.** The caller waits up to 20 seconds for the whole collection, which is published only at its end. When it times out, the caller gets the previous snapshot, marked `timed_out` per service, rather than per-provider partial results.
- **Unknown stays unknown.** In `suggest_service`, an unknown room is `null` with `quota: "unknown"`, ranked after known room. Stale readings are marked and ranked after fresh ones with the same room.

**Differences from the first draft:**
- `freshness` is one map per result, keyed by service id, instead of a block inside each service entry. The same shape works for model lists, which are not grouped by service.
- `collected_at` is the existing top-level `updated_at`.
- `provider_read_at` is named `read_at`.
- The service-level `source` was dropped: estimated windows already say `estimated: true`. `configured` and per-metric sources arrive with free-model limits (§2.3).
- `unsupported` is not reported yet. M1 cannot tell "no endpoint" from "no reading". M2, which separates catalogues from usage, adds it.
- The refresh outcome values are the ones listed in docs/MCP.md. `shared` is internal, and `failed` became `unchanged`.

`claim_capacity` (§3) checks the pool's room on the latest reading. Plugins that need fresher quotas call `list_candidates` with `max_age_seconds` first.

## 2. Providers and execution candidates (M2, M3)

### 2.1 Separate facts

| Record | Contents |
| --- | --- |
| `CredentialReference` | An opaque id (`cred_…`) plus the service, the source app, `windows` or `wsl`, and whether it is usable. Never the secret, its path or the account identity |
| `ModelCapability` | Service and model id, API family, context window, and support for tools, streaming and reasoning. Unknown capabilities are `null` |
| `QuotaObservation` | Scope (`account`, `project` or `model`) and metric (`requests_per_minute`, `tokens_per_minute`, `requests_per_day`, `concurrency`, `credits`). Limit, used and remaining, all nullable. Window and reset, with an IANA time zone when the provider resets on local time. Source and coverage. For free models, see §2.3 |
| `ExecutionCandidate` | Model + runtime + credential reference, plus `free` (below), authentication state and the quota observations that apply |

### 2.2 Free status

```json
"free": { "status": "confirmed_free", "kind": "zero_price", "source": "catalogue", "checked_at": "…" }
```

- `status`: `confirmed_free`, `paid` or `unknown`.
- `kind`: `zero_price`, `free_tier`, `promotional_credit` or `local`.
- A missing price is not zero, and a discovered key proves nothing about price.

### 2.3 Limits of free models

M2 implements this section in `list_free_models`; [docs/MCP.md](../MCP.md#free-models-and-their-limits) is the reference. Still to come: the `observed` counters, which come from the outcomes released with capacity leases (M4, §3).

A coordinator can split work sensibly only if it knows, for each free model, what one request may contain and how much the model can still take. Free tiers are mostly made of limits, so these limits are part of the contract from M2 on. They do not wait for delegation.

Today (0.8.6) `list_free_models` returns the context window, tool and reasoning support, and the last free day. It returns no output cap and no rate limit.

Every free model, and every execution candidate, carries a `limits` object:

```json
"limits": {
  "per_request": {
    "context_tokens":    { "value": 131072, "source": "catalogue" },
    "max_output_tokens": { "value": 8192,   "source": "catalogue" },
    "max_input_tokens":  { "value": null,   "source": "unknown" }
  },
  "pool": "openrouter:free",
  "rates": [
    { "metric": "requests_per_minute", "limit": 20, "used": null, "remaining": null,
      "window": "rolling", "scope": "account", "source": "documented",
      "reference": "https://…", "checked_at": "2026-11-02" },
    { "metric": "requests_per_day", "limit": 50, "used": 12, "remaining": 38,
      "resets_at": "2026-11-03T01:00:00+01:00", "reset_zone": "UTC",
      "scope": "account", "source": "provider_reported", "coverage": "all_clients" },
    { "metric": "concurrent_requests", "limit": null, "source": "unknown" }
  ],
  "observed": {
    "in_flight": 1, "requests_last_minute": 3, "requests_today": 12,
    "last_rate_limited_at": "…", "retry_after_until": null,
    "coverage": "ai_meter_jobs_only"
  },
  "headroom": { "can_start_now": true, "max_new_jobs_now": 1, "blocking": null }
}
```

The numbers above are placeholders. M2 verifies each provider's real limits.

**Per-request caps** decide whether a task fits at all:
- context window;
- maximum output tokens;
- maximum input tokens, when the provider caps input separately from the context window.

A model that cannot hold the task's input, or produce the output it needs, is filtered out before any rate limit is considered.

**Pools.** Many free limits are shared:
- one quota can cover every free model of an account;
- Gemini limits are counted per model, but within the whole project;
- several services can sit behind one key.

`pool` names the counter a model draws from. Two models with the same `pool` are not independent capacity. The coordinator and the scheduler must never add their limits together.

**Metrics:**
- `requests_per_minute`, `requests_per_hour`, `requests_per_day`;
- `tokens_per_minute`, `tokens_per_day`, `input_tokens_per_minute`;
- `concurrent_requests`;
- `credits`, for promotional credit.

Unknown values are `null`. A limit the provider does not have is left out. A limit the provider has but AI Meter cannot read is kept, with `"source": "unknown"`.

**Sources,** in order of trust:

| `source` | Meaning |
| --- | --- |
| `provider_reported` | Read from the provider's API for this account (for example a key or quota endpoint) |
| `catalogue` | Read from the provider's model list (context window, output cap) |
| `documented` | Published by the provider. Kept in the service definition JSON with `reference` and `checked_at`, never hard-coded in C# |
| `configured` | Set by the user in Settings, for example a stricter cap or a tier AI Meter cannot detect |
| `observed` | Counted by AI Meter from the jobs it ran and from the rate-limit responses they received |
| `unknown` | The limit exists, but no value is available |

**Coverage.**
- `observed` counters cover only what AI Meter can see: its own jobs, and possibly the local session logs of supported runtimes (to be checked in M2).
- Requests made elsewhere with the same account (other apps, other PCs) are invisible. Only a `provider_reported` value includes them.
- Every counter therefore says what it covers (`all_clients`, `this_pc_observed`, `ai_meter_jobs_only`), and `remaining` is never derived from observed counters alone.

**Headroom.** This is a summary the coordinator can act on directly:
- `can_start_now`;
- `max_new_jobs_now` for this pool, after jobs already running or reserved;
- `blocking`, which names the limit that stops it (for example `requests_per_day`) and when that limit resets.

With no known concurrency limit, `max_new_jobs_now` is at most 1.

**What AI Meter controls.** AI Meter controls admission: how many jobs it starts on each pool, and when. It does not control how often a runtime sends requests during a job. Whether Pi can be paced or capped per request is a question for the M4 spike. Until then, the documentation must not claim that AI Meter enforces per-minute limits inside a job.

### 2.4 Selection

1. **Filter.** Drop candidates that:
   - lack the required capabilities or runtime authentication;
   - fail the cost policy (`free_only` admits only `confirmed_free`);
   - have per-request caps (§2.3) below the task's needs, such as `min_context` or `min_output_tokens`.
2. **Exclude.** Drop candidates whose pool is in backoff, or has `can_start_now: false`, or has no capacity left after reservations.
3. **Order.**
   1. Candidates whose binding limit has known remaining room come first, ordered by that room, where the binding limit is the tightest relevant rate in their pool.
   2. Confirmed-free candidates with unknown limits come next. They run at a concurrency of 1.

   Ties are broken deterministically by service id and model id.
4. **Label estimates.** Duration or quality estimates may adjust the order only if the result labels them as estimates.

M3 implements this section in `src/Candidates.cs` and `list_candidates`; [docs/MCP.md](../MCP.md#choosing-where-to-run-a-task-list_candidates) is the reference. The differences from this draft:
- The cost policies are `free_only` and `free_or_unconfirmed`. A paid policy waits until model lists carry prices for paid models.
- `CredentialReference` is the `credential` id with its runtime and place. The source app is not returned, since only each agent's own keys count.
- Runtimes are a data table of agents (`agents/*.json`, plus the user's `%APPDATA%\AIMeter\agents`), not a single one. Only agents with a verified dispatch for their place are candidates (Pi in WSL, OpenCode in WSL and on Windows); none of them is required. Agents found without one (Codex, Claude Code, Gemini CLI, Antigravity CLI, Cursor Agent, Crush, Pi on Windows) are listed in `runtimes.unsupported` with a reason, so a plugin can tell the user why. The table's shape is in [docs/MCP.md](../MCP.md#agent-definitions).
- Every running WSL distribution is scanned, each candidate carrying its `distro`; the Linux home is the one holding any agent's folder.
- `warnings` flags candidates a plugin must watch: `openrouter_byok_fallback` today.
- **Best effort.** Models whose service list leaves the price or a needed capability unstated can be filled in from an agent's own model list (`models` in its definition). They are returned apart, in `best_effort`, only with `include_best_effort`, labelled with `estimated` and `unverified`, as point 4 requires. NVIDIA and Gemini have no ordinary candidates otherwise ([docs/MCP.md](../MCP.md#best-effort-candidates)).
- Reservations and the `observed` counters come with capacity leases (M4, §3).

**`list_candidates` (M3, read tool).** It takes the task's `requirements` and a `cost_policy`. It returns the ordered candidates, each with its `limits` and `headroom`, plus the reason each excluded candidate was dropped.

This lets a coordinator plan several tasks at once, such as how many parallel jobs to start and on which pools, before it calls `claim_capacity`. `claim_capacity` still re-checks and reserves capacity atomically, because the answer can change between the two calls.

### 2.5 Provider notes

- **Gemini Developer API.**
  - Quotas belong to the project, not the key.
  - Daily reset is midnight `America/Los_Angeles`, using a time zone with daylight saving, never a fixed offset.
  - Kept separate from Vertex, Gemini CLI OAuth and Antigravity.
- **NVIDIA API Catalog.**
  - The aliases `nvidia` and `nvidia-nim` are distinct providers.
  - The catalogue and authentication are exposed even when no remainder is readable.

## 3. Dispatch and capacity leases (M4)

AI Meter runs no jobs ([ADR 0002](../adr/0002-delegation-through-coordinator-plugins.md)). A coordinator plugin runs the agent. AI Meter describes how to run it, and keeps cooperative leases on pool capacity.

M4 implements this section in `src/Dispatch.cs`, `src/Leases.cs` and the MCP tools; [docs/MCP.md](../MCP.md#running-a-task-describe_dispatch-and-capacity-leases) is the reference. Differences from this draft:
- `describe_dispatch` also takes `mode` and `from`.
- `claim_capacity` derives the pool from service and model rather than taking it.
- `renew_capacity` exists.
- A rate limit without Retry-After closes the pool for 60 s.

### 3.1 `describe_dispatch`

Input: a candidate from `list_candidates`, identified by `service`, `model`, `runtime`, `where` and, in WSL, `distro`.

Output:

```json
{
  "runtime": "pi", "where": "wsl", "version": "1.4.0",
  "program": "pi",
  "arguments": ["--provider", "openrouter", "--model", "z/large:free", "…"],
  "task_input": "stdin",
  "output": { "format": "jsonl", "final": "…" },
  "workspace_flag": null,
  "notes": "…"
}
```

- The description comes from a data table per agent (`agents/*.json`, see [docs/MCP.md](../MCP.md#agent-definitions)): per verified place, a version range, and the argument template per mode with `{provider}`, `{model}` and `{workspace}`, plus the environment. The M4 spike filled the built-in rows, and nothing is written from memory. [spike-agents.md](spike-agents.md) has the evidence (Pi 1.x, OpenCode 1.x).
- Besides the fields above, each row says how to tell the end (Pi: `agent_settled`), where the outcome is (Pi: the last assistant `stopReason`, not the exit code), how to count requests and tokens, where each request's price appears (`output.cost`: Pi's `usage.cost.total`, OpenCode's `step_finish.part.cost`), and how to stop the agent. Agents price requests from their own model list, so a price above zero means a paid request, but zero does not rule out a BYOK fallback billed by the provider.
- `warnings` (such as `openrouter_byok_fallback`) repeats the candidate's warnings.
- `arguments` is an array. The plugin starts the program without a shell and passes the task as data, through stdin or a single argument, as `task_input` says. There is no field for an arbitrary command, and the task text never enters the description.
- `where: "wsl"` means the plugin starts the agent inside WSL, which must already be running. AI Meter never starts WSL.
- An agent without a verified row returns `unsupported`, and the reason. Before that, distinct errors name an unknown runtime, AI Meter still reading its first data, an agent not set up here, key discovery turned off, and an agent without its own key for the service.
- An agent npm installed in WSL needs `node`: its folder goes next to the agent's in the generated PATH (nvm, fnm and asdf included), and without one the answer is `unsupported`. On Windows only a native `.exe` is described, never a `.cmd` or `.ps1` wrapper.

### 3.2 Leases

| Tool | Input | Output |
| --- | --- | --- |
| `claim_capacity` | `pool`, `service`, `model`, `runtime`, `ttl_seconds` (default 1800, at most 7200), `client` (a free label such as `claude-code`) | `lease_id`, `expires_at`; or `refused` with `blocking` when the pool has no room |
| `renew_capacity` | `lease_id`, `ttl_seconds` | New `expires_at`. Long tasks renew before expiry |
| `release_capacity` | `lease_id`, `outcome` (`completed`, `failed`, `cancelled`, `timed_out`), `requests`, `rate_limited` (count, and `retry_after` when the agent saw one), `tokens` (input, output, when known) | `released`. Idempotent: a second release changes nothing |

- **Headroom.** While a lease is active, `max_new_jobs_now` of its pool counts it, for every caller of `list_free_models` and `list_candidates`. A claim that would exceed the pool's room is refused, and the check and the claim happen together.
- **Expiry.** A lease that reaches `expires_at` without a release ends with outcome `unknown`. A crashed plugin cannot block a pool.
- **Rate limits.** A released `rate_limited` with `retry_after` puts the pool in backoff, as an AI Meter read that gets a `429` does.
- **Observed counters.** Released outcomes feed `observed` (§2.3) with `coverage: "ai_meter_leases_only"`. Values the provider reports stay authoritative.
- **What is stored.** Leases are kept in a small local store, with their own retention. It holds pool, service, model, runtime, client, times and the outcome counters above. Never task text, code, logs, paths or keys.

### 3.3 What the plugins own

M5 implements this in [plugins/ai-meter](../../plugins/ai-meter/README.md), one plugin for both Claude Code and Codex. These belong to the plugin, not to AI Meter:
- status, result and cancellation;
- background runs and their logs;
- the worktree for tasks that change a repository: read-only by default, never merged automatically;
- retries after a `429`. Before any side effect, a plugin may pick another candidate. After one, it never retries blindly.

`free_only` never falls back to a paid candidate.

## 4. Versions

| Item | Versioned by |
| --- | --- |
| App | `AssemblyInfo.cs` / `Controller.Version` |
| MCP contract | `serverInfo.version` stays the app version. A `contract` number in the `initialize` instructions is raised only on breaking changes |
| Lease store | Its own schema version, migrated transactionally like the history archive |
