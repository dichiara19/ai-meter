# Architecture

AI Meter is a Windows app (C# 5, .NET Framework 4.8, WPF) that reads usage and limits of AI services and local agents, keeps a local history, and serves both through an MCP server. A separate coordinator plugin (`plugins/ai-meter`, Node.js) delegates tasks to coding agents on free models, or on the user's subscription plans when allowed ([ADR 0003](adr/0003-delegation-on-subscription-plans.md)), using AI Meter to choose and pace them.

## Delegation: trust boundaries

```
coordinator (Claude Code / Codex)
  └─ plugin MCP server (node, stdio)          plugins/ai-meter/scripts/mcp-server.mjs
       └─ job worker (detached node process)  scripts/lib/jobs.mjs
            ├─ relay (PowerShell, mcp.ps1) ── named pipe ── AI Meter (McpServer.cs, Candidates.cs, CandidatesPlans.cs, Dispatch.cs, Leases.cs)
            └─ coding agent (Pi / OpenCode;    started without a shell, task on stdin
               Claude Code / Codex on a plan)  plan agents: clean environment, sign-in checked first (scripts/lib/plan.mjs)
                                               managed Pi: the copy AI Meter keeps (ManagedPi.cs), own configuration folder
                 └─ model provider (network)  receives the task and every file the agent reads
```

| Boundary | What crosses it | What holds it |
| --- | --- | --- |
| Coordinator → plugin | Task text, workspace path, pins. | The plugin validates every JSON-RPC envelope and caps request lines at 1 MiB; it never runs a shell. |
| Plugin → AI Meter | Tool calls over the relay and the per-user named pipe (ACL: current user only). | Answers are matched by id and every request has a deadline; the pipe caps request lines. AI Meter returns no key: only an opaque credential id and the dispatch plan. Only the lease tools write state (the lease book: pool, model, agent, times and counters). |
| AI Meter → plugin: the plan | Program, argument list, environment, working folder. | Built from `agents/*.json` for verified agents, versions and places only; never the task, never a key. `read_only` is enforced by what the agent is given (Pi's tool list, OpenCode's per-agent permissions), not by an agent's name. |
| Plugin → agent's folder | The files the agent can see. | `read_only`: a snapshot of the files Git tracks or would track, without names that suggest secrets. `modify`: a new worktree from the last commit. Neither is a sandbox: a path named outside them can still be read. |
| Agent → provider | The task and every file the agent reads. | Out of AI Meter's control: providers of free models may log or train on them. Models an agent prices are not offered; the first priced request stops the agent, after it was paid. |
| Plan agent → billing | Requests on the user's Claude or ChatGPT plan. | Never pay-as-you-go (ADR 0003): AI Meter excludes a plan with extra usage or credits on, below the user's reserve or without a fresh reading of a plan sign-in; the plugin starts the agent from a clean environment (no API key, base URL or coordinator session reaches it), checks its sign-in first, and stops it when its events show billed usage. |
| Managed Pi (optional) | One pinned Pi release in two archives (Windows, Linux), each downloaded only when the user presses its button in Settings; the keys of the services the user ticked. | `ManagedPi.cs` checks each archive against a SHA-256 in AI Meter's sources, discards anything else and unpacks only inside its own folder. Inside WSL the verified bytes are fed to the distribution's own `tar` through `wsl.exe` ([ADR 0005](adr/0005-managed-pi-inside-wsl.md)): the one program AI Meter starts there, only during an installation the user asked for, never starting WSL itself; the copy lives in `~/.aimeter/managed/pi` of the Linux home, with a configuration folder of mode 700. The copy on Windows lives in the shared folder (`Settings.LocalSharedFolder`: `%LOCALAPPDATA%\AIMeter`, which does not roam with the profile, or `%USERPROFILE%\.aimeter` in the Store build, whose AppData is private to the package) under `managed\pi`, outside `PATH`, with its own configuration folder, so the user's `~/.pi` is never read or written. The archive is hashed and unpacked through one file handle held without sharing, in a new staging folder for each attempt; leftovers of earlier attempts are deleted and never promoted. AI Meter also binds the copy's shell (Pi's `shellPath` in the copy's own `settings.json`, when Git for Windows is found) and starts it with `PI_OFFLINE=1`, so it downloads nothing while it works. Each copy's `auth.json` is plain text, as in any Pi installation (a second file, in the Linux home, when the copy inside WSL is installed); it holds only ticked services, is rewritten when a choice or a key changes, and is deleted when nothing is ticked, when the copy is not installed and at every start. Pi's tools are not confined to the task's folder, so the agent itself can read that file. No MCP tool installs, updates or removes a copy, or returns a key ([ADR 0004](adr/0004-managed-pi-for-delegation.md), [ADR 0005](adr/0005-managed-pi-inside-wsl.md), [MCP.md](MCP.md#managed-pi)). |
| Agent process tree | Processes the agent starts. | The worker owns them: a Linux process group, signalled as a whole; `taskkill /T` on Windows when stopping (processes left after a normal exit on Windows are not tracked). A job whose worker died is settled by the next coordinator call. |

**Windows and WSL.** AI Meter runs on Windows and finds agents on Windows and in every running WSL distribution. The managed Pi on Windows serves Windows folders and Windows coordinators only; the managed Pi inside WSL serves coordinators on Windows and in WSL (from Windows it is started through `wsl.exe` like any agent in WSL, with its own environment). A plugin on Windows starts an agent in WSL through `wsl.exe -e env … <program>`: no Linux shell, so no shell profile is read, and an agent whose key is only exported in a profile is refused from Windows. A plugin in WSL starts agents in its own distribution only, by their Linux path; agents in other distributions, and Windows agents, are skipped with the reason.

## Local history

### Responsibilities

The standalone application keeps its C# 5 / .NET Framework 4.8 build, existing dependencies and native WPF surfaces. The history feature has explicit boundaries:

| Component | Responsibility | Lifetime / dependencies |
| --- | --- | --- |
| `Controller` (`Controller.cs` and its `Controller*.cs` parts) | Compose the services; schedule collection; handle lock/sleep, settings, notifications and windows. | Application host. Creates no history worker or UI polling loop. |
| `HistoryCollector` | Read changed local sources and emit whitelisted numeric events. | Uses existing read-only adapters and the already-running WSL check. Source cursors belong to the archive transaction. |
| `HistoryStore` | Own the schema, transactions, scopes, deduplication, feedback, export and deletion. | Receives an explicit archive path. Opens and disposes connections per operation; serialises operations through one gate. |
| `HistoryDatabase` | Bind parameters, execute commands, stream rows and release SQLite handles. | Windows SQLite ABI only; all user/source values use bindings. The repository owns SQL and schema choices. |
| `HistoryAnalysis` | Build coherent read-only projections; attach the detector's stored episodes and readiness. | No windows or controls. The archive retains facts; projections never rewrite them. |
| `ConsumptionAnalysis` | Validate quota readings into intervals; summarise personal reference ranges; decide readiness; produce fast and sustained evidence. | Pure and deterministic: times, zone and data are passed in. No UI, registry, network or clock reads. |
| `ConsumptionMonitor` in `ConsumptionAlerts.cs` | Process new readings after each history batch; persist intervals, scope state and episodes; claim notices; list overlapping sessions. | Runs on the collection worker inside one archive transaction. Failures are logged and never stop collection or quota alerts. |
| `HistoryWindow` | Translate and present projections; accept explicit rating/export/deletion actions. | UI thread for controls; database work runs on tasks. Uses the existing Fluent palette and standard keyboard-operable WPF controls. |
| `HistoryVisuals` | Reusable settings-style cards, messages, chart axes and accessible disclosure controls. | Presentation only; uses the shared palette and system colours in high contrast. |
| `StringsHistory` | Feature translations. | Five translations plus English source keys, registered through the existing localisation engine. |

There is one writer and one storage implementation. An interface hierarchy or dependency-injection container would add no current capability. If a future host requires a second implementation, extract that contract at the point of need. Existing provider collection and forecasts remain independently usable.

Version 2 (consumption tables added transactionally to version 1 archives) uses SQLite `user_version`, transactions, WAL, foreign keys and indexes for metric/time/session queries. A newer, unsupported schema is rejected without deleting or recreating the archive. Provider/account/metric/reset boundaries stay explicit; costs, token counters, quotas and feedback are different facts with different units. No translated caption is parsed to reconstruct a number.

Persistent settings and history windows share the same native chrome helper, including the extended glass frame required by the backdrop. Content uses a separate layer and padded cards rather than the legacy tab-control frame. Charts use recorded points only; single samples, missing values and reported zeroes have distinct explanations. Consumption readiness and episodes come from the same stored evidence as notifications, so dashboard filters and chart limits never change detection, and a new or unidentified account cannot be presented as having passed a check.

Daily projections resolve local day boundaries through .NET rather than SQLite's optional local-time conversion. Indexed UTC buckets identify candidate dates, and range aggregates retain the original event counters without loading transcripts. Day boundaries account for daylight-saving transitions, including skipped or repeated midnights.
