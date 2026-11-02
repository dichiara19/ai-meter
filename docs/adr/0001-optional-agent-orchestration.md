# ADR 0001: Optional local agent orchestration

- **Status:** proposed. Merging the M0 pull request accepts it.
- **Date:** 2026-11-02
- **Plan:** [docs/plans/agent-orchestration.md](../plans/agent-orchestration.md)

## Context

AI Meter monitors AI usage. Since 0.8.6, connected AI apps can read that data through a read-only MCP server.

The next step is delegation: a coordinator should be able to hand a bounded task to a local executor that uses a free model. The coordinator is Claude Code or Codex. The first executor is Pi. AI Meter would choose the model from what it knows about quotas and free catalogues, then start the job and let the coordinator watch, read and cancel it.

That crosses several rules that hold today:
- AI Meter only reads usage. It never runs inference.
- Network requests go only to each service's own usage endpoint.
- The MCP server is read-only, and its tools add no network requests.
- The app has no dependencies beyond WPF and the Windows APIs.
- WSL is never started.

The rules must not be quietly stretched. Monitoring and execution must stay separate, so that each keeps its own promises.

## Decision

1. **Execution is an optional responsibility, off by default.**
   - Monitoring keeps every current rule unchanged.
   - Execution needs its own switch in Settings and at least one authorised workspace folder.
   - With execution off, AI Meter behaves exactly as 0.8.6 does.
2. **The MCP server keeps two tool groups.**
   - Read tools stay read-only. Any refresh they perform goes only to usage and catalogue endpoints (see [the contracts](../plans/orchestration-contracts.md)).
   - Job tools (`start_job`, `get_job`, `list_jobs`, `cancel_job`, `get_job_result`) appear in `tools/list` only while execution is on.
   - No tool ever returns a key, a token or an account identity. Credentials appear only as opaque references.
3. **AI Meter does not perform inference and does not hand credentials to the runtime in the MVP.**
   - The runtime (Pi) calls the model provider with its own configuration and sign-in.
   - A candidate model is eligible only if the runtime is already authenticated for that provider.
   - Keys found in other apps (`FindKeys`) authorise monitoring only, never execution.
   - Passing a stored key to a runtime would need a later ADR.
4. **Runtimes are detected, never installed.**
   - AI Meter finds the installed Pi and its version, shows what is missing, and offers an explicit setup action.
   - No MCP read and no job installs anything.
   - In WSL, a runtime is used only while WSL is already running.
5. **The core stays C# 5 / .NET Framework 4.8 with no NuGet or Node.**
   - Pi, a Node program, runs as a separate child process.
   - Two shapes are considered for the runner: inside `AIMeter.exe`, or a second executable built from the same sources by `build.ps1`. The M4 spike chooses between them.
   - The runner must:
     - own its process trees through a Windows job object;
     - survive or reconcile an AI Meter restart;
     - never touch processes it did not start.
6. **Isolation is about Git, not security.**
   - A task that modifies a repository runs in a new Git worktree, and is never merged automatically into the user's working tree.
   - A worktree is not a sandbox. File, shell and network permissions are the runtime's, and the documentation must say so.
7. **Job data is separate from history.**
   - Tasks, logs and patches can contain code and prompts.
   - They live in their own local store, with their own retention and deletion.
   - They are never written to the history archive, which stores no prompts or source code.
8. **No claimed savings without measurement.**
   - A saving counts only when it is measured, with the coordinator, intermediary, executor and verification each measured separately.
   - Counted tokens are not equated with subscription quota saved.

## Consequences

- **Documents.**
  - CONTRIBUTING and SECURITY gain the execution boundaries now.
  - The README privacy policy changes only in the milestone that ships execution (M4). It must not describe features that do not exist yet.
- **Network.**
  - New monitoring endpoints for NVIDIA and Gemini fall under the existing rule: the service's own endpoints, only for services the user turned on.
  - The runtime's inference traffic is the runtime's, and is documented as such.
- **Versions.** The app version, the MCP/job contract and the job store schema are versioned separately (see the plan, *Versions and release*).
- **Rollback.** Turning execution off leaves monitoring and the read tools intact. Running jobs are cancelled first.
- **Cost.** M1–M3 improve the read side on their own and can ship without the runner.

## Alternatives considered

- **Leave delegation entirely to the coordinator.** Coordinators can already start Pi through a shell. They would not see quota freshness, free-model status or account scope, so routing would stay guesswork. This is still the fallback if the spikes fail.
- **A Node or NuGet-based runner inside the WPF process.** Rejected: it breaks the dependency rule and couples the UI to the runtime.
- **Pass discovered keys to the runtime through environment variables.** Rejected for the MVP. It would turn monitoring consent into execution consent and widen where secrets travel.
- **Execution in a second product.** It would duplicate collection and settings. One app with an opt-in boundary is simpler.
