# Orchestration contracts (draft)

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

`start_job` (§3) still always makes sure the quotas it relies on are fresh, unless a shared collection finished less than 30 seconds earlier.

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

M2 implements this section in `list_free_models`; [docs/MCP.md](../MCP.md#free-models-and-their-limits) is the reference. Two parts are still to come: the `observed` counters, which need AI Meter's own jobs (M4), and `list_candidates` (M3).

A coordinator can split work sensibly only if it knows, for each free model, what one request may contain and how much the model can still take. Free tiers are mostly made of limits, so these limits are part of the contract from M2 on. They do not wait for the runner.

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
- `CredentialReference` is the `credential` id with its runtime and place. The source app is not returned, since only Pi's own keys count.
- The `observed` counters and reservations come with the runner (M4).

**`list_candidates` (M3, read tool).** It takes the same `requirements` and `cost_policy` as `start_job`. It returns the ordered candidates, each with its `limits` and `headroom`, plus the reason each excluded candidate was dropped.

This lets a coordinator plan several tasks at once, such as how many parallel jobs to start and on which pools, before it calls `start_job`. `start_job` still re-checks and reserves capacity atomically, because the answer can change between the two calls.

### 2.5 Provider notes

- **Gemini Developer API.**
  - Quotas belong to the project, not the key.
  - Daily reset is midnight `America/Los_Angeles`, using a time zone with daylight saving, never a fixed offset.
  - Kept separate from Vertex, Gemini CLI OAuth and Antigravity.
- **NVIDIA API Catalog.**
  - The aliases `nvidia` and `nvidia-nim` are distinct providers.
  - The catalogue and authentication are exposed even when no remainder is readable.

## 3. Jobs (M4)

These tools are listed only while execution is turned on ([ADR 0001](../adr/0001-optional-agent-orchestration.md), decision 2).

### 3.1 `start_job`

```json
{
  "task": "Add a regression test for …",
  "workspace": "C:\\src\\project",
  "mode": "read_only | modify_in_worktree",
  "requirements": { "tools": true, "min_context": 64000, "min_output_tokens": 8000 },
  "cost_policy": "free_only",
  "candidate": "optional explicit candidate id",
  "timeout_seconds": 1800,
  "max_turns": 40,
  "max_output_bytes": 1048576,
  "idempotency_key": "client-chosen string"
}
```

- `workspace` must be inside a folder authorised in Settings.
- `task` is passed to the runtime as data, never through a shell. There is no field for an arbitrary command.
- `start_job` returns at once with `job_id` and `queued`. No call stays open for the life of the job.
- Repeating `idempotency_key` returns the existing job. It never starts a second process.

### 3.2 States

```
queued → running → completed | failed | cancelled | timed_out
                 ↘ cancelling → cancelled
interrupted: AI Meter restarted and could not reconcile the process
```

Every transition is stored with its time.

On restart, AI Meter reconciles running jobs by process id **and** process start time, so a reused PID is never mistaken for a job.

### 3.3 Other tools

| Tool | Returns |
| --- | --- |
| `get_job` | State, timestamps, candidate used, worktree path, last progress line |
| `list_jobs` | Jobs started by AI Meter, newest first. Never sessions started by other programs |
| `cancel_job` | Cancels the job's own process tree. Idempotent |
| `get_job_result` | The result below. Output and logs are paged with `cursor` and `max_bytes` |

`resume_job` is added only after the spike shows that the runtime can really resume.

### 3.4 Result

- summary;
- exit status;
- changed files, with the patch or the worktree commit;
- checks actually run, each with its outcome;
- known tokens and cost, with the limits of that measurement;
- truncation flags.

Logs and patches follow the job store's retention, not the history archive's.

### 3.5 Rate limits and failover

- On `429`, honour `Retry-After`. Then either requeue the job or pick another allowed candidate before any side effect.
- A rate-limit response puts the whole pool in backoff, not just the job, and is recorded in that pool's `observed` counters (§2.3).
- Each job's result reports the requests and rate-limit responses the runtime exposes, so the counters improve as jobs run.
- After a side effect, never retry blindly. Fail explicitly, or continue from a stored checkpoint.
- `free_only` never falls back to a paid candidate.

## 4. Versions

| Item | Versioned by |
| --- | --- |
| App | `AssemblyInfo.cs` / `Controller.Version` |
| MCP contract | `serverInfo.version` stays the app version. A `contract` number in the `initialize` instructions is raised only on breaking changes |
| Job store | Its own schema version, migrated transactionally like the history archive |
