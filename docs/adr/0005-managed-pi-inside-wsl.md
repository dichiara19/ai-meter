# ADR 0005: A managed Pi inside WSL, and one setup for delegation

- **Status:** proposed. Merging its pull request accepts it.
- **Date:** 2026-11-24
- **Issue:** [#101](https://github.com/dichiara19/ai-meter/issues/101), part of #17
- **Amends:** [ADR 0004](0004-managed-pi-for-delegation.md) decision 4 (Windows folders only) and the part of [ADR 0001](0001-optional-agent-orchestration.md) decision 4 that keeps AI Meter from writing or starting anything inside WSL, for the managed copy's installation only. [ADR 0002](0002-delegation-through-coordinator-plugins.md) and [ADR 0003](0003-delegation-on-subscription-plans.md) stay in force.

## Context

ADR 0004 gave AI Meter an optional copy of Pi on Windows, for tasks on Windows folders started from Windows. Two things stayed out.

A user who works in WSL without an agent there cannot delegate: the copy is not offered to a coordinator in WSL, because the plugin there does not start Windows programs and Git does not work across the boundary.

On Windows the copy runs commands through Git Bash, or through PowerShell when there is no Git for Windows. Most coding tasks expect a Linux shell with Git and Unix tools. The user's own Pi in WSL already gives that to a coordinator on Windows: the plugin starts it through `wsl.exe` on the Windows folder, and delegated tasks have run that way since the first live tests. Two limits of that route are known: inside a working copy the plugin made on Windows, Git commands from WSL do not find the repository, and a project that builds only with Windows tools cannot be built from Linux.

Pi publishes a standalone build for Linux in the same release as the one for Windows.

Delegation should not ask a user to know what an agent, a place or a shell is. AI Meter can see what the PC has and say what is missing.

## Decision

1. **A managed copy may live in two places.**
   - On Windows, as decided in ADR 0004.
   - Inside WSL: the Linux build of the same pinned release, in the distribution AI Meter is set to. One distribution, in this phase.
   - Each place is installed and removed on its own, only by the user's action in Settings.
2. **Inside WSL first, where there is WSL.**
   - Where AI Meter finds WSL, Settings proposes the copy inside WSL first. Without WSL it proposes the Windows copy, and says when Git for Windows is missing.
   - A coordinator in WSL is offered the copy in its own distribution. A coordinator on Windows is offered both when both are installed.
   - Between the two copies, for the same model, a preference in Settings says which comes first: inside WSL unless the user chooses Windows. A plugin can still pin one for a task, as for any runtime.
   - The user's own agents still come before any managed copy, and a managed copy is never used for a subscription plan.
3. **Installed inside WSL by AI Meter, from Windows.**
   - AI Meter downloads the archive on Windows and checks it against a SHA-256 kept in its sources, as for the Windows build. The verified bytes are unpacked inside the distribution, in the user's home, outside `PATH`.
   - This is the one case in which AI Meter starts a program inside WSL: the distribution's own `tar`, to unpack with the files' permissions, and only during an installation the user asked for. No MCP read and no task does it.
   - The distribution must be running: AI Meter does not start WSL to install, and says so.
4. **Its own configuration and keys, as on Windows.**
   - The copy inside WSL has its own configuration folder in the user's Linux home, readable by that user only, and never reads or writes `~/.pi`.
   - The user chooses services once. AI Meter keeps the key file of every installed copy in step, with the same rules as ADR 0004: only ticked services, only plain keys, withdrawn when unticked, counted by a revision beside the file.
   - A copy inside WSL starts from an environment that holds none of the coordinator's credentials, like the Windows copy.
5. **One guided section in Settings.**
   - "Delegated tasks" says what AI Meter found on this PC (the user's agents, WSL, Git for Windows, the managed copies), what is missing, and offers the action that sets delegation up.
   - When a task cannot run, the plugin tells the user the next step from what `list_candidates` reports: install a copy, tick a service's key, start WSL.
6. **Store and privacy** as in ADR 0004 decision 7: the Store build keeps managed copies off until its listing discloses the downloads, now two archives from the same release.

## Consequences

- A user with WSL and no agent can delegate from Windows and from WSL after one action in Settings, with a Linux shell.
- AI Meter now writes inside a WSL distribution and starts `tar` there, during installation. A second plain-text key file exists, in the user's Linux home.
- Each adopted Pi release has two archives to pin and two dispatches to verify.
- Tasks on Windows folders run from WSL keep the known limits: Git commands inside the plugin's working copy do not find the repository, access to `/mnt/c` is slow on projects with many small files, and Windows-only toolchains do not run. The Windows copy is there for those projects.
- One distribution only: a user with several chooses which one in AI Meter's settings, as for everything else AI Meter reads in WSL.

## Alternatives considered

- **Start the Windows copy from WSL through interop.** Reading and editing files works, but commands and Git do not work across the boundary.
- **Only inside WSL.** Simpler, but it leaves out users without WSL and projects that build only with Windows tools.
- **Unpack on Windows and copy the files into WSL.** No program started inside WSL, but the files would lose their permissions and links, and the program would not be executable.
- **Choose between the copies by looking at the project.** Fewer settings, but a wrong guess would be hard for the user to understand; a stated preference and a per-task pin are predictable.
