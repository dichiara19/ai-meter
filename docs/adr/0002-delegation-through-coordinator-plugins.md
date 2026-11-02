# ADR 0002: Delegation through coordinator plugins

- **Status:** proposed. Merging its pull request accepts it.
- **Date:** 2026-11-02
- **Supersedes:** [ADR 0001](0001-optional-agent-orchestration.md) decisions 2 (job tools), 5 (the runner), 6 (isolation, which moves to the plugins) and 7 (job data). It also changes decision 1: AI Meter itself no longer executes anything.
- **Plan:** [docs/plans/agent-orchestration.md](../plans/agent-orchestration.md)

## Context

ADR 0001 put a job runner inside AI Meter: it would start the coding agent, watch it, cancel it and keep its results.

Coordinators can already drive agents themselves (the plan, *Spikes*, read 2026-11-02):
- The official Claude Code → Codex plugin starts `codex app-server` from a small script called by a Claude Code subagent. It keeps background jobs in its own files, with status, result and cancel commands.
- The community Codex → Claude Code plugins run `claude -p` in headless mode, the same way.

The maintainer chose, on 2026-11-02, to build on that pattern instead of a runner of AI Meter's own. A runner is the heaviest part of the plan:
- processes and process trees;
- cancellation;
- reconciliation after a restart;
- responsibility for what the agent does.

The plugins already solve the coordinator side of each of these.

## Decision

1. **AI Meter does not start, supervise or cancel tasks.**
   - A plugin for each coordinator (Claude Code, Codex) runs the chosen coding agent through the agent's own non-interactive interface, in the background where the coordinator supports it.
   - The plugin owns status, result and cancellation, as the existing plugins do.
2. **AI Meter decides where to run and keeps the books.**
   - **Selection:** `list_candidates` (M3).
   - **Dispatch description:** for a candidate, a structured description of how to run that agent on that model: program, argument list, model and provider flags, output format and where it runs (Windows or WSL). It comes from a data table per agent.
     - The task text is never part of it.
     - It is never a shell string: the plugin passes the task as data.
   - **Cooperative capacity leases:**
     - `claim_capacity` takes a slot in a pool. It returns a lease id with an expiry, and the slot counts against that pool's headroom for every other caller.
     - `release_capacity` returns the slot with an outcome: exit status, the requests and rate-limit answers the agent reported, and tokens when known.
     - A lease that is not released expires on its own, so a crashed plugin cannot block a pool.
   - **Observed counters:** released outcomes feed the `observed` counters of the pool (contracts §2.3). Counts that the provider reports stay authoritative.
3. **Leases are cooperative, and AI Meter says so.**
   - Leases bind only clients that use them. Other apps using the same account are invisible, as they already are.
   - Results state this coverage (`ai_meter_leases_only`).
   - A pool's room never comes from leases alone.
4. **The lease tools are local bookkeeping.**
   - They start no process and make no network request.
   - They store only the pool, the model, the agent, times and outcome counters. Never task text, code, logs or keys.
   - They are listed with the other tools. Installing a plugin is the user's opt-in to delegation.
5. **The plugins live in this repository, outside the core.**
   - They live under `plugins/`, versioned with AI Meter, one per coordinator, written in what that coordinator's plugins use (Node scripts for Claude Code and Codex).
   - They are not built into `AIMeter.exe`. The core keeps C# 5, no NuGet and no Node (ADR 0001 decision 5, dependency part).
6. **Isolation and permissions belong to the plugin, with safe defaults.**
   - A delegated task is read-only unless the coordinator asks for changes.
   - A task that changes a repository runs in a new Git worktree, and is never merged automatically.
   - A worktree is not a sandbox: the agent's own permissions apply, and the plugin's documentation says so.
7. **ADR 0001 decisions 3, 4 and 8 stay in force.**
   - No key is handed to an agent, and only agents' own keys make a candidate.
   - No particular agent is required: delegation uses whichever supported agents are set up. "Supported" means an agent in AI Meter's data table (`agents/*.json`) with a dispatch verified for its place, today Pi in WSL and OpenCode in WSL and on Windows. Other agents found are reported as unsupported, with the reason, never guessed. Nothing is installed by a read.
   - No saving is claimed without measurement.

## Consequences

- **Less code in AI Meter.** There is no process control, no job store and no restart reconciliation. M4 becomes dispatch descriptions plus leases; M5 becomes the plugins.
- **Weaker reservation.** Two coordinators that both use leases cannot overbook a pool. One that does not use them can. The headroom fields say what they cover.
- **No cancel from AI Meter.** Cancelling is the plugin's job. AI Meter only sees the lease end, or expire.
- **Agents' rate limits.** AI Meter learns about them only through `release_capacity`. Leases that expire without a report count as outcome unknown.
- **Privacy.** There is less to protect: AI Meter stores no task text or logs. The README privacy policy changes when the lease tools ship.
- **Rollback.** Uninstall the plugin. Leases expire on their own, and the read tools are unaffected.

## Alternatives considered

- **A runner owned by AI Meter** (ADR 0001). It gives atomic reservation for every client and full control of the job. It was rejected for its weight and its ownership of processes. It can return later behind the same leases if cooperative reservation proves too weak.
- **Plugins without leases.** Simpler, but two coordinators could pick the same pool at the same moment, and AI Meter would never see the agents' rate limits.
