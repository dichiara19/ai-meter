# Orchestration contracts (draft)

Proposed contracts for milestones M1–M4 of the [agent orchestration plan](agent-orchestration.md). Every name below is a proposal. Each milestone finalises its part in code and in [docs/MCP.md](../MCP.md), and this file then points there.

The existing conventions of the MCP server stay:
- `snake_case` fields;
- local ISO 8601 times;
- percentages from 0 to 100;
- each result returned as `structuredContent` and as the same JSON in a text block.

New fields are additive. Any change in meaning is listed in the release notes.

## 1. Freshness of read tools (M1)

### 1.1 Arguments

`get_limits`, `get_spend`, `list_free_models` and `suggest_service` accept two new arguments:

| Argument | Type | Meaning |
| --- | --- | --- |
| `refresh` | boolean | Ask the provider now, ignoring AI Meter's own interval. Backoff and `Retry-After` still apply |
| `max_age_seconds` | integer ≥ 0 | Refresh only data older than this |

`list_sessions` and `list_local_models` accept `refresh` to re-read local state. That reads local files and `127.0.0.1` only.

**Default (open decision).**
- **Recommended:** no refresh unless the caller asks. This keeps the 0.8.6 promise that tools add no network requests, and lets agents opt in with `max_age_seconds`.
- **Alternative:** `get_limits` and `suggest_service` refresh quotas by default, as the original plan proposed.

`start_job` (§3) always makes sure the quotas it relies on are fresh, unless a shared collection finished less than 30 seconds earlier.

### 1.2 Per-service freshness block

Every service in a result carries a `freshness` object:

```json
"freshness": {
  "collected_at": "2026-11-02T12:00:05+01:00",
  "provider_read_at": "2026-11-02T11:58:00+01:00",
  "state": "fresh",
  "refresh": "refreshed",
  "source": "provider_reported",
  "reason": null
}
```

| Field | Values |
| --- | --- |
| `collected_at` | When AI Meter last tried to read the provider for this service |
| `provider_read_at` | When the data shown was actually read. It is not overwritten by later failed attempts |
| `state` | `fresh`, `stale`, `unavailable` (tried and failed), or `unsupported` (no endpoint for this metric) |
| `refresh` | `not_requested`, `refreshed`, `cached` (fresh enough), `shared` (joined a collection already running), `backoff`, `timed_out` or `failed` |
| `source` | `provider_reported`, `local_estimate` or `configured` |
| `reason` | A short machine-readable reason when the state is not `fresh` (`rate_limited`, `auth_expired`, `network`, `no_endpoint`…) |

### 1.3 Collection rules

- **Single flight.** Concurrent requests for one provider share one collection.
  - Cancelling one caller does not cancel a collection that others are waiting on.
  - Periodic collection and forced refresh never mutate the same provider state at the same time.
- **Separate timers.** Quota and catalogue refreshes are invalidated separately: `fetchedAt` for quotas, and `freeNextFetch` or its successor for catalogues.
- **Timeout.** A refresh has a bounded timeout (proposed: 10 s per provider). It returns partial results, with each service marked as above.
- **Unknown stays unknown.** An unknown remainder is `null`, never 100.

### 1.4 `suggest_service`

- `remaining_percent` is `null` when no limit is known. It is no longer 100.
- Sorting:
  1. services with a known remaining room, by that room;
  2. services whose free models are confirmed free but whose quota is unknown, flagged with `"quota": "unknown"`;
  3. stale data, which is ranked below fresh data with the same room.

## 2. Providers and execution candidates (M2, M3)

### 2.1 Separate facts

| Record | Contents |
| --- | --- |
| `CredentialReference` | An opaque id (`cred_…`) plus the service, the source app, `windows` or `wsl`, and whether it is usable. Never the secret, its path or the account identity |
| `ModelCapability` | Service and model id, API family, context window, and support for tools, streaming and reasoning. Unknown capabilities are `null` |
| `QuotaObservation` | Scope (`account`, `project` or `model`) and metric (`requests_per_minute`, `tokens_per_minute`, `requests_per_day`, `concurrency`, `credits`). Limit, used and remaining, all nullable. Window and reset, with an IANA time zone when the provider resets on local time. Source and coverage (`all_clients` or `this_pc_only`) |
| `ExecutionCandidate` | Model + runtime + credential reference, plus `free` (below), authentication state and the quota observations that apply |

### 2.2 Free status

```json
"free": { "status": "confirmed_free", "kind": "zero_price", "source": "catalogue", "checked_at": "…" }
```

- `status`: `confirmed_free`, `paid` or `unknown`.
- `kind`: `zero_price`, `free_tier`, `promotional_credit` or `local`.
- A missing price is not zero, and a discovered key proves nothing about price.

### 2.3 Selection

1. **Filter.** Drop candidates that lack the required capabilities or runtime authentication, or that fail the cost policy. `free_only` admits only `confirmed_free`.
2. **Exclude.** Drop candidates in backoff, or whose reserved local capacity is exhausted.
3. **Order.** Known remaining room first, then confirmed-free candidates with unknown quota, which run at a cautious concurrency of 1. Ties are broken deterministically by service id and model id.
4. **Label estimates.** Duration or quality estimates may adjust the order only if the result labels them as estimates.

### 2.4 Provider notes

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
  "requirements": { "tools": true, "min_context": 64000 },
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
- After a side effect, never retry blindly. Fail explicitly, or continue from a stored checkpoint.
- `free_only` never falls back to a paid candidate.

## 4. Versions

| Item | Versioned by |
| --- | --- |
| App | `AssemblyInfo.cs` / `Controller.Version` |
| MCP contract | `serverInfo.version` stays the app version. A `contract` number in the `initialize` instructions is raised only on breaking changes |
| Job store | Its own schema version, migrated transactionally like the history archive |
