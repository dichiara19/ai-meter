# Agent orchestration plan

The plan for taking AI Meter from reading usage to controlled delegation:
- a coordinator (Claude Code or Codex) hands a bounded task to a local executor that uses a free model;
- the executor is whichever supported coding agent the user already has: no particular one is required;
- AI Meter picks the model from what it knows about quotas and free catalogues, then lets the coordinator watch, read and cancel the job.

| Document | Contents |
| --- | --- |
| [ADR 0001](../adr/0001-optional-agent-orchestration.md) | Boundaries: what changes and what does not |
| [orchestration-contracts.md](orchestration-contracts.md) | Proposed MCP, candidate and job contracts |
| [ai-meter-orchestration-status.md](ai-meter-orchestration-status.md) | Current state, evidence and the working log |

This file is a plan. Nothing here exists in code until a milestone's pull request is merged, and the status log is the record of that.

## Requirements

- Live readings on request, with an explicit forced refresh.
- Reuse of keys that are already configured, respecting the existing *find keys* setting.
- For every free model, the limits a coordinator needs to assign work, each with its source and freshness:
  - per-request caps: context, output and input;
  - request and token rates per minute and per day;
  - concurrency;
  - the shared pool the model draws from.
- OpenRouter, Experiential Labs, NVIDIA API Catalog and Gemini Developer API.
- Delegation to a full executor, with status, result and cancellation. Delegation is offered according to the agents the user has: one adapter per agent, none required.
- Claude Code and Codex as coordinators, in the foreground or background where the client supports it.
- No promised savings without measuring the coordinator, any intermediary, the executor and the checks.

## Architecture

AI Meter keeps collection and the UI, chooses where a task runs (`list_candidates`), describes how to run the chosen agent, and keeps cooperative capacity leases and the outcomes they report.

A plugin per coordinator (Claude Code, Codex) runs the agent in the background and owns status, result and cancellation ([ADR 0002](../adr/0002-delegation-through-coordinator-plugins.md)). AI Meter has no job runner.

The first version targets Windows. It uses WSL only when WSL is already running. Standalone Linux and macOS are out of scope.

## Milestones

| ID | Deliverable / branch | Depends on | Exit criteria |
| --- | --- | --- | --- |
| M0 | Baseline, ADR, contracts, epic issue / `docs/agent-orchestration-plan` | — | SHAs and versions recorded; boundaries updated in CONTRIBUTING and SECURITY; MVP scope decided |
| M1 | MCP freshness / `feat/mcp-freshness` | M0 | Real clients verified on Windows and WSL; refresh bypasses the right caches; timeout and concurrency tested; Settings → AI apps verified. The Store relay path moved to M6 |
| M2 | NVIDIA and Gemini discovery, catalogues and free-model limits / `feat/provider-discovery` | M0, M1 contract | Keys found with fixtures; catalogues independent of quotas; `list_free_models` returns `limits` and `pool` for every free model of every supported provider, with source and `checked_at`; documented limits live in the provider JSON; unknown stays unknown |
| M3 | Quota-aware candidate selection / `feat/execution-candidates` | M1, M2 | `list_candidates` with `headroom`; selection is deterministic; per-request caps filter; pools never add up; `free_only`, quota scope and `429` tested |
| M4 | Agent spike, dispatch descriptions and capacity leases / `feat/dispatch-leases` | M3 | For at least two agents, a verified non-interactive invocation on a free model, described as data. `describe_dispatch` returns it without the task text and without a shell string. `claim_capacity` and `release_capacity` make headroom account for leases, expire on their own, and feed `observed` counters. No task text, code or key is stored |
| M5 | Claude Code and Codex plugins / `feat/coordinator-plugins` | M4 | One real task from each coordinator, through its plugin: choose, claim, run in the background, status, result, cancel, release. Read-only by default; changes in a new worktree; measured cost of coordinator, intermediary and agent |
| M6 | Stabilisation, UX, packaging, release candidate | M1–M5 | End-to-end tests; upgrade from 0.8.6 verified; artefacts verified, including the MCP relay in the Store package; version decided |

M1 and M2 can proceed independently after M0. A coding agent works on one milestone at a time unless it is explicitly told otherwise.

A milestone may land as several small pull requests.

**MVP for M4 and M5:**
1. At least two agents with a verified dispatch description: Pi, already installed, and a second one. The description format must not be shaped around one agent.
2. A read-only task, then a task that changes a repository in a new worktree, each run from a coordinator plugin with a lease.

Not in the MVP:
- a universal installer;
- multi-provider resume;
- remote execution;
- a visual integration identical to native subagents;
- reservation that binds clients which do not use leases.

## Spikes

Each spike records the version tested and what it observed, in the status log.

- **Coordinator plugins (read 2026-11-02).** These show the two ways agents are driven today.
  - **Claude Code → Codex.** [openai/codex-plugin-cc](https://github.com/openai/codex-plugin-cc) at `db52e28`, official.
    - `/codex:rescue` starts a real Claude Code subagent (`codex:codex-rescue`, on Sonnet, with only Bash), which makes one Bash call to `codex-companion.mjs task`.
    - That script starts **`codex app-server`**, Codex's JSON-RPC protocol over stdio (`thread/start`, `turn/start`, `turn/interrupt`, `thread/resume`), optionally through a shared broker on a local socket.
    - Background jobs are a detached `task-worker` process with job files, read by `/codex:status`, `/codex:result` and `/codex:cancel`.
    - So Codex is driven indirectly, through its server protocol, and the intermediary subagent costs tokens.
  - **Codex → Claude Code.** No official plugin; several community ones: `pejmanjohn/cc-plugin-codex` (`9445dcb`), `lzhgus/cc-plugin-codex` (`b198c3d`), `andiradulescu/cc-plugin-codex` (`244b83a`), `KewkLW/codex-plugin-claude` (`e02c81b`), `farmage/codex-plugin-claude-subagent` (`d686e91`).
    - They all launch the `claude` CLI directly in headless mode: `claude -p … --output-format json` or `stream-json`.
    - Background jobs are a detached process writing a log.
  - **Consequences.**
    - `codex app-server` and `claude -p` are candidate adapter interfaces next to Pi's and OpenCode's.
    - These plugins delegate to another paid agent, not to free models. Whether Codex can run free models through an OpenAI-compatible provider is still to be checked.
    - A lighter route is also possible: AI Meter chooses the model and pool (`list_candidates`) and counts consumption, while the coordinators' own plugins run the task. **Chosen on 2026-11-02:** see [ADR 0002](../adr/0002-delegation-through-coordinator-plugins.md).
- **Agents (Pi first, as it is installed here; then at least one more).**
  - Pick the structured interface available in the pinned version.
  - Check events, EOF, errors, cancellation, resume and the usage it reports.
  - Check whether it exposes per-request counts, rate-limit responses and a way to cap output or pace requests.
  - Do not rely on remembered flags.
- **Claude Code.**
  - Review `openai/codex-plugin-cc` at a pinned SHA before copying its contract.
  - Earlier notes mention a background Bash job and an intermediary subagent. Confirm them.
- **Codex.** Find and read a concrete source for the reverse adapter. None has been identified yet, so none can be relied on.
- **Client UI.** Document what each client really shows. MCP alone does not make a native subagent.
- **Cost.**
  - Measure the coordinator, intermediary, executor and checks separately, on one short and one medium task, against direct execution.
  - Do not equate counted tokens with subscription quota saved.

## Tests

| Area | Cases |
| --- | --- |
| MCP | Handshake, schema and errors, unknown tool, notifications, abrupt disconnection, several clients, shutdown without blocked threads |
| Refresh | Targeted bypass of quota and catalogue intervals, single flight, timeout, partial failure, stale cache, cancellation, overlap with periodic collection |
| Credentials | *Find keys* off; Windows and WSL; WSL not running; aliases and variables; malformed JSON and JSONC; custom directories; several keys. No secret in any response or log |
| Catalogues | Missing price is not zero, expired model, unknown context or tools, missing quota, paid tier, expired sign-in |
| Quotas | UTC and Pacific resets across daylight saving, shared project quotas, `429` and `Retry-After`, concurrent reservations, usage from other clients |
| Free-model limits | Output cap below the task's need; context fits but output does not; two models in one pool; unknown concurrency gives at most one job; documented limit older than its `checked_at` policy; observed counters never shown as account-wide; headroom after reservations |
| Dispatch and leases | No task text in a dispatch description; arguments as an array, never a shell string; leases counted in headroom; expiry; double release; outcome counters; no task text or key stored |
| Plugins | Paths with spaces, tasks containing shell characters, background run, status, result, cancellation, huge output, worktree isolation, lease always released or left to expire |
| Integration | Real Pi and a second agent on a free model; Claude Code and Codex through their plugins; Windows, and WSL already running |
| Compatibility | Existing MCP reads, previous settings and history, startup, script/EXE/MSIX packages, themes and translations |

Automated tests use:
- a controllable clock;
- loopback servers;
- fake credentials;
- fake agents for the plugins.

They never use real keys or personal configuration. Live smoke tests are separate. They use enabled accounts and redacted logs, and record the client and runtime versions.

```powershell
powershell -NoProfile -Sta -ExecutionPolicy Bypass -File tests/run.ps1
powershell -ExecutionPolicy Bypass -File build.ps1 -NoInstall
powershell -ExecutionPolicy Bypass -File build.ps1 -NoInstall -Package
```

A test that cannot be run is recorded as **NOT RUN**, with the reason and the gate it leaves open. A Linux run does not replace the Windows WPF build and packaging.

## Git protocol

**At the start of a session:**
- Read CONTRIBUTING, docs/MCP.md, docs/PROVIDERS.md and the workflow.
- Run `git status --short --branch`, `git fetch origin --prune` and `git log -5 --oneline`. Check the open pull requests, tags and releases.
- Never delete, commit or stash someone else's changes implicitly. Use a fresh worktree if the tree is busy.

**While working:**
- Keep one topic per pull request. Commits are in English with imperative titles.
- Record the base and head SHAs that tests ran on. A test count from an older commit is not evidence.
- After a parent pull request is squash-merged, rebase dependants without replaying its commits, and check the diff against the new base.
- No indiscriminate `reset --hard`, `clean` or force push.
- Update the documentation and translations in the milestone that changes the behaviour.
- Before merging:
  - check the final diff;
  - check CI on the new head;
  - resolve all comments;
  - complete the PR template;
  - merge with squash.

## Versions and release

**Version numbers.**
- **0.9.0:** operational MCP and the new providers (M1–M3).
- **0.10.0, or 1.0.0-rc.N:** dispatch, leases and the plugins, as a preview.
- **1.0.0:** once the operational contract and the job lifecycle are stable and tested.

Confirm these numbers again if other releases ship in between. The app, the MCP contract, the lease store schema and each plugin are versioned separately.

**The release workflow publishes a release for every `v*` tag.** It does not distinguish prereleases.
- Before any RC tag, change the workflow to set prerelease or draft correctly.
- Never push a tag "to try".

**Release checklist:**
- **Branch:** `main` contains only finished milestones, or features that are off and labelled experimental.
- **Commit:** the tree is clean, the remote head is verified, and CI passes on the chosen commit.
- **Version numbers:** align `Controller.Version` and the assembly versions. Search with `rg` for other occurrences, without rewriting examples or history.
- **Notes and docs:** update RELEASE_NOTES, README, docs/MCP.md, provider docs and migration notes.
- **MSIX:** the version comes from `AssemblyInfo`, is numeric and always increases. An RC suffix never goes into it.
- **Plugins:** they ship through each coordinator's plugin mechanism, not inside the app packages, and their install instructions name the AI Meter version they need.
- **Upgrade:** test upgrade from 0.8.6, and rollback, on isolated copies of settings and history.
- **Tag:** tag only the verified commit. Never move a published tag.
- **Artefacts:**
  - check both ZIPs and the MSIX, and record checksums;
  - do not claim the build is signed while SignPath is inactive;
  - the MSIX is a CI artefact only, and submitting it to the Store is a separate step.
- **After publishing:** check the release URL, the tag, the downloads and the install. Only then mark the milestone `released`.

**Rollback:**
- turn execution off and keep monitoring and the read tools;
- let plugin tasks finish or cancel them from the coordinator before updating;
- keep other clients' configurations.

Fix forward. Never recreate an existing tag.

## Sources

**Repository:**
- PR #15 and PR #14;
- the v0.8.5 and v0.8.6 releases;
- CONTRIBUTING.md;
- `.github/workflows/build.yml`.

**External, to be re-read at the version that is implemented:**
- [Pi providers](https://github.com/earendil-works/pi/blob/main/packages/coding-agent/docs/providers.md)
- [pi-nvidia-nim-provider](https://github.com/Tibbee/pi-nvidia-nim-provider)
- [OpenCode providers](https://opencode.ai/docs/providers/)
- [NVIDIA NIM](https://docs.api.nvidia.com/nim/docs/product)
- [NVIDIA API quickstart](https://docs.api.nvidia.com/nim/docs/api-quickstart)
- [Gemini rate limits](https://ai.google.dev/gemini-api/docs/rate-limits)
- [Gemini billing](https://ai.google.dev/gemini-api/docs/billing)
- [Gemini troubleshooting](https://ai.google.dev/gemini-api/docs/troubleshooting)

**To read in the spikes:** [openai/codex-plugin-cc](https://github.com/openai/codex-plugin-cc), and the reverse-adapter project mentioned in the original discussion (not yet identified).
