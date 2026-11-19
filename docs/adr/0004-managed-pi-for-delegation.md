# ADR 0004: A managed Pi for delegation

- **Status:** proposed. Merging its pull request accepts it.
- **Date:** 2026-11-19
- **Issue:** [#98](https://github.com/dichiara19/ai-meter/issues/98), part of #17
- **Amends:** [ADR 0001](0001-optional-agent-orchestration.md) decisions 3 and 4, for the managed copy only. [ADR 0002](0002-delegation-through-coordinator-plugins.md) and [ADR 0003](0003-delegation-on-subscription-plans.md) stay in force.

## Context

Delegation uses a coding agent the user already has, in the place where the task's folder is. Verified today: Pi in WSL, OpenCode in WSL and on Windows, and Claude Code and Codex in WSL for subscription plans. A user on Windows with no agent, or with agents only in WSL, cannot delegate a task on a Windows folder, and installing an agent means Node, npm or WSL.

Coordinator plugins that hand work from Claude Code to Codex, or the other way, ask for the other CLI to be installed. AI Meter can ask the same for subscription plans, where nothing else is possible: without an API key, a plan is reachable only through its vendor's CLI with the user's sign-in. For services reached with a key (free models and free tiers) the agent is interchangeable, and Pi already speaks to all of them.

Pi is MIT-licensed and publishes a standalone build for Windows that needs no Node. A live test on 2026-11-19 with Pi 1.5.2 (`pi-windows-x64.zip`, 59.1 MB, 191 MB unpacked), driven by a local stand-in for the model, found that it:
- accepts the arguments of `agents/pi.json` and the task on stdin, and emits the same JSON events as in WSL;
- keeps to its own configuration folder with `PI_CODING_AGENT_DIR`, without creating `~/.pi`;
- reads, writes and edits files without any bash, where only commands fail; its PowerShell tool works with Windows PowerShell 5.1;
- silently uses WSL's `bash.exe` when Git Bash is missing and `System32` is on `PATH`;
- cannot use Git on a WSL folder reached through `\\wsl.localhost`;
- can leave a child command running after `taskkill /T /F` (#37).

A request to a real model from the Windows build was not part of that test.

## Decision

1. **Two levels, and the user's agent comes first.**
   - The default stays "use the agent you have". Nothing is downloaded unless the user asks.
   - The managed Pi is an optional addition for services reached with a key. It is never used for a subscription plan.
   - In each place, a verified agent of the user's wins. The managed copy is a separate runtime that `list_candidates` offers only where no verified Pi of the user's is found, unless the user chooses otherwise in Settings. Both are named in the answer, so the choice is visible.
2. **Installed only by an explicit action in Settings.**
   - Settings › AI apps › Delegated tasks offers the download and its removal. No MCP read and no task installs, updates or removes anything (ADR 0001 decision 4 stands for those).
   - AI Meter downloads one pinned Pi release from the project's official releases and checks it against a SHA-256 kept in AI Meter's own sources. A different file is discarded.
   - A newer Pi is adopted only by a new AI Meter version, after its dispatch is verified: Pi does not promise a stable event format.
   - The copy lives in AI Meter's data folder, outside `PATH`, and nothing else on the PC is changed.
3. **Its own configuration, and keys only by the user's choice.**
   - The managed copy runs with its own configuration folder and never reads or writes the user's `~/.pi`.
   - For each service the user ticks, AI Meter writes that service's key into the managed copy's own key file. This is the later decision that ADR 0001 decision 3 asked for: a stored key reaches a runtime, but only this one, only for the services chosen, and it can be withdrawn.
   - No MCP tool returns a key, and the plugin receives none: it starts the copy with the path of its configuration folder, which is not a secret. A candidate still needs a runtime that holds a key for its service.
   - The key file is plain text, as in any Pi installation, where AI Meter's own store is encrypted for the Windows user. Settings says so beside the choice.
4. **Windows folders only, in this phase.**
   - The managed copy serves tasks whose folder is on Windows. A folder in WSL keeps using the agents installed there.
   - A coordinator in WSL is not offered the managed copy: the plugin there does not start Windows agents, and Git fails across the boundary.
5. **A fixed shell, and tasks that say what they can do.**
   - The plugin starts the managed copy with a shell it has chosen, never with one Pi finds: Git Bash when installed, otherwise the PowerShell tool. WSL's `bash.exe` is never used.
   - The dispatch description states whether commands can be run. A read-only task needs no shell.
6. **The plugin still runs the agent** (ADR 0002 decision 1). AI Meter describes the dispatch and keeps the leases; it starts no task.
7. **Store and privacy.**
   - The Store listing discloses the optional download at the start of its description, and the download happens only on the user's request (Store Policies 7.20: 10.2.2, 10.2.3, 10.2.4, 10.1.5).
   - If certification refuses it, the managed copy stays in the GitHub builds, and the Store build keeps "use the agent you have".
   - The README's list of network destinations gains GitHub's release hosts, for this download only. The sentence that the Store version never contacts GitHub changes accordingly.

## Consequences

- A Windows user with a key and no agent can delegate after one action in Settings.
- AI Meter takes on a third-party program's versions: each adopted Pi release needs its dispatch verified, and a security fix in Pi reaches users only with an AI Meter release.
- A key the user chose now also lies in a plain-text file under AI Meter's data folder.
- 59 MB are downloaded and 191 MB kept, only for those who ask.
- Tasks that must run commands still depend on Git Bash or PowerShell, and stopping a task can leave a child process until #37 is done.
- Users with only Windows and a subscription plan gain nothing: Claude Code and Codex on Windows are still unverified.

## Alternatives considered

- **Only verify the user's own agents on Windows (#36).** Cheaper and still worth doing, but it leaves the installation to the user.
- **An agent loop inside AI Meter.** No dependency at all, but AI Meter would maintain a coding agent, against ADR 0001 decision 3.
- **Bundle Pi in the package.** No download at run time, but 59 MB more for every user and a third-party program inside the Store package.
- **Always use the managed copy.** More repeatable results, but it overrides an installation the user chose and keeps up to date.
- **Hand the key to the plugin for each task.** No key file, but an MCP tool would return a key, against ADR 0001 decision 2.
