# Agent orchestration: status

The working log for the [agent orchestration plan](agent-orchestration.md). Update it at the end of every working session. Record what was actually done, not what was planned. Keep test evidence short and free of secrets.

Allowed states: `planned`, `in_progress`, `blocked`, `in_review`, `merged`, `released`.
- `merged` needs the commit on `main`.
- `released` needs a published tag, release and artefacts.

## Milestones

| Milestone | State | Branch / PR | Base / head SHA | Tests and evidence | Blockers / next action |
| --- | --- | --- | --- | --- | --- |
| M0 Baseline, ADR, contracts | merged | [PR #16](https://github.com/dichiara19/ai-meter/pull/16), squash-merged as `f051b41`; epic [#17](https://github.com/dichiara19/ai-meter/issues/17) | `b39d449` / `692917b` | Documentation only. `tests/run.ps1` on the base: 89 passed. CI `build` passed on every PR commit, the last being `692917b` (run 41435557504) | — |
| M1 MCP freshness | merged | [PR #18](https://github.com/dichiara19/ai-meter/pull/18), squash-merged as `972c7f3` | `f051b41` / `5eacffc` | `tests/run.ps1`: 95 passed. Live: relay from Windows; Claude Code, Codex and Antigravity CLI in WSL; Settings → AI apps. CI `build` green | Store relay path moved to M6 |
| M2 Provider discovery | merged | [PR #21](https://github.com/dichiara19/ai-meter/pull/21), squash-merged as `4441b54` | `649bff3` / `048550f` | Rebased tree: 102 tests passed. CI `build` green (run 41470098975). Real NVIDIA and OpenRouter lists parsed | Live NVIDIA and Gemini reads with real keys: **NOT RUN** |
| M3 Execution candidates | merged | [PR #22](https://github.com/dichiara19/ai-meter/pull/22), squash-merged as `79c28c2` | `4441b54` / `2b2d967` | Rebased tree: 108 tests passed. CI `build` green (run 41471025123). Live agent scan on this PC | — |
| M4 Dispatch descriptions and capacity leases | merged | [PR #26](https://github.com/dichiara19/ai-meter/pull/26), squash-merged as `ba8d1e3`; ADR 0002 as [PR #23](https://github.com/dichiara19/ai-meter/pull/23), `830837b` | `830837b` / `e52285d` | Rebased tree: 114 tests passed. CI `build` green (run 41475549785). Pi and OpenCode in WSL started from Windows with AI Meter's descriptions, live | — |
| M5 Claude Code and Codex plugins | in_progress | `feat/coordinator-plugins`, from `main` | `ba8d1e3` / not yet pushed | `node --test`: 11 passed on Windows and in WSL; `tests/run.ps1` unchanged. Live: delegations from Windows and from WSL, Pi and OpenCode, read-only, `modify` and cancel; Claude Code 2.1.316 in WSL through the plugin; cost measured | A task driven by Codex's own model: **NOT RUN** (usage limit). Coordinators on native Windows and the interactive screens: **NOT RUN** |
| M6 Stabilisation and release candidate | planned | — | — | — | Needs M1–M5 |

## Baseline (verified 2026-11-02)

When the baseline was taken, `gh` was not installed on the development PC, so the GitHub state below came from the public REST API (`api.github.com/repos/dichiara19/ai-meter`) and `git fetch`. GitHub CLI 2.103.3 has since been installed.

| Item | Observed |
| --- | --- |
| `main` | `b39d449fd609a9a9b07b4e029f7e0c0c15779152`, "Release 0.8.6" |
| Latest release | `v0.8.6`, published 2026-11-02 10:31 UTC. Not a draft or prerelease. Assets: `AIMeter.zip`, `AIMeter-script.zip` and the history images |
| Assembly metadata | `0.8.6.0`; informational version `0.8.6` |
| MCP server (PR #15) | Squash-merged as `43c5075`. The last PR CI run (head `5ce3f06`) passed |
| CI on `b39d449` | `build` passed for both the `main` push and the `v0.8.6` tag |
| MSIX | Built as a CI artefact only. It is not attached to the GitHub release |
| Open issues | Dependabot #1–#5 only. No epic and no milestones |

**Differences from the plan's original snapshot.** The plan was written against `fcfa9bf` (0.8.5), when PR #15 was still open. Since then:
- the MCP server has shipped in 0.8.6;
- M1 starts from `main`;
- upgrade tests in M6 start from 0.8.6.

Three things the PR marked as unverified are still unverified after the release, and remain M1 exit criteria:
- the integration with real clients;
- the Settings → AI apps page;
- the relay path in the Store package.

## Code facts re-checked on `b39d449`

| # | Fact from the plan | Result |
| --- | --- | --- |
| 1 | MCP reads the snapshot. `DefinedProvider` has its own intervals: OpenRouter 5 min, Experiential Labs 15 min, catalogues 360 min, all ×3 when saving power | Confirmed (`ProviderDefined.cs:46-48`, `providers/*.json`) |
| 2 | `InvalidateNetwork` clears `fetchedAt` but not `freeNextFetch` | Confirmed (`ProviderDefined.cs:35`) |
| 3 | The free-model catalogue hangs off `Usage`, so it needs a usage reading | Confirmed (`Model.cs:122`). `Suggest` also skips providers with `Usage == null` |
| 4 | `suggest_service` treats "no known limit" as 100% room | Confirmed (`McpServer.cs:293`) |
| 5 | `list_sessions` has no job identity or control | Confirmed |
| 6 | The pipe host handles one request at a time, line by line | Confirmed (`McpServer.cs:404`) |
| 7 | `KeyFinder` returns the first key and caches it for 60 s | Confirmed (`KeyFinder.cs:37-50`) |
| 8 | `KeyFinder` reads fixed home-relative paths, so custom Pi directories are not covered | Confirmed (`KeyFinder.cs:21-29`) |
| 9 | `!command` values are ignored. `$VAR`, `${VAR}` and `{env:VAR}` are resolved. Bare variable names and JSONC are not | Confirmed (`KeyFinder.cs:82-89`). `Json.ParseAny` behaviour on comments not yet tested |

## Open decisions for the maintainer

1. **Accept [ADR 0001](../adr/0001-optional-agent-orchestration.md).** Resolved: accepted when PR #16 was merged.
2. **Default refresh behaviour of read tools.** Resolved 2026-11-02: only on request ([contracts §1](orchestration-contracts.md#1-freshness-of-read-tools-m1)).
3. **Credentials for the runner in the MVP.** The recommendation is that the runtime uses only its own sign-in, and AI Meter never passes a key to it (ADR 0001, decision 3).
4. **Epic issue.** Resolved: [#17](https://github.com/dichiara19/ai-meter/issues/17), opened once GitHub CLI 2.103.3 was installed and signed in.

## Log

- **2026-11-02, M5 implemented: one plugin for Claude Code and Codex.**
  - **`plugins/ai-meter`.** One folder carries both manifests:
    - `.claude-plugin/plugin.json` and `.mcp.json` for Claude Code;
    - `.codex-plugin/plugin.json` and `codex-mcp.json` for Codex.
    - The marketplaces are `.claude-plugin/marketplace.json` and `.agents/plugins/marketplace.json` at the repository root.
    - Both coordinators get the same MCP server (`delegate`, `delegate_result`, `delegate_status`, `delegate_cancel`, `delegate_candidates`) and the same `delegate` skill.
    - It uses Node's standard library only.
    - **Why an MCP server rather than commands:** the task travels as JSON, never through a shell.
  - **How a job runs.**
    - Jobs run in a detached worker, recorded under `~/.aimeter/jobs`.
    - The worker calls `list_candidates`, then `describe_dispatch` before `claim_capacity`, so no lease is taken for an agent it cannot start. `docs/MCP.md` now lists the steps in that order.
    - It starts the program as described, writes the task to stdin and closes it.
    - It reads Pi's and OpenCode's events, then releases the lease with the outcome, the requests, the tokens and any `429`.
    - Cancelling stops the agent; so does a timeout.
    - `modify` runs in a new worktree on `ai-meter/<job id>`. Nothing is committed, merged or removed.
  - **Live, on this PC.** Every lease was released, and AI Meter's observed counters added up.
    - **From Windows:**
      - OpenCode on Windows, 2 requests;
      - Pi in WSL, 2 requests;
      - a `modify` task in a scratch repository: the fix was in the worktree, and the checkout was untouched;
      - a cancel: the agent ended in about 1 s, with no process left.
    - **From WSL:** OpenCode in WSL. Its Windows variant was skipped with AI Meter's reason.
    - **Through the MCP server,** natively on Windows and from Codex's plugin cache in WSL.
    - **Claude Code 2.1.316 in WSL** installed the plugin from the local marketplace and delegated a task headless. Codex 0.163.0 installed it and lists the server, but its model could not run because the account hit its usage limit.
  - **Found live:**
    - AI Meter answers only `status` while it reads its first data, so the plugin waits for it;
    - the first changed file lost its first letter (`alc.py`), because Git's status lines were trimmed. Fixed, with a regression test.
  - **Cost.** Single runs with Sonnet 5.5 as coordinator; details in the [plugin README](../../plugins/ai-meter/README.md#does-it-save).
    - **Short task:** $0.217 direct, $0.301 delegated.
    - **Medium task:** $0.370 direct, $0.255 delegated, but with a weaker answer.
    - The prompt cache favours whichever run of a pair goes second.
  - **Tests and CI.** 11 tests with a fake AI Meter and fake agents, on Windows and in WSL. CI runs them on Windows, and in a new `plugin-linux` job.
  - **Gaps:**
    - AI Meter's order ties within a pool, so the first model is alphabetical. The skill advises pinning a coding model.
    - Leases are not renewed: one claim covers the timeout plus two minutes.
    - AI Meter was reinstalled from `main` for the live checks; the installed copy had been older.


- **2026-11-02, the chain is merged.**
  - The maintainer squash-merged everything into `main`:
    - #18 (M1) as `972c7f3`;
    - #19 (Antigravity CLI) as `b5d547b`;
    - #20 (copy confirmation) as `649bff3`;
    - #21 (M2) as `4441b54`;
    - #22 (M3) as `79c28c2`;
    - #23 (ADR 0002) as `830837b`;
    - #26 (M4) as `ba8d1e3`.
  - **Rebasing the stacked PRs.** A force push of a rebased branch was refused by the session's permissions. So each stacked PR was instead:
    1. rebased locally and tested;
    2. then `main` was merged into its branch, with the result set to the tested tree and checked to be identical;
    3. CI then ran on the new head.
  - Temporary branches and worktrees were removed after checking their content was on `main`.
  - M5 starts on `feat/coordinator-plugins` from `ba8d1e3`.

- **2026-11-02, M4 implemented.**
  - **`src/Dispatch.cs`.** Verified rows: Pi in WSL; OpenCode in WSL and on Windows.
    - Programs are found by detection; versions are read from npm's `package.json`, without running anything.
    - Arguments are lists and the task goes on stdin; OpenCode is isolated; Windows uses the native `.exe`; WSL is reached through `wsl.exe -e env PATH=… <agent>`.
    - Anything unverified answers unsupported.
  - **`src/Leases.cs`.** Claim, renew and release.
    - Expiry with an unknown outcome; a pool backoff after an agent's rate limit.
    - Daily outcome counters for 30 days, in `%APPDATA%\AIMeter\leases.json`, schema 1.
  - **Headroom.** `Candidates.Room` counts active leases and closes a fully leased pool (`leased`).
  - **MCP.** `describe_dispatch`, `claim_capacity`, `renew_capacity` and `release_capacity`; `observed` in `list_free_models`.
  - **Agent detection** now records the provider alias the key is kept under and the agent's program path.
  - **Live, from Windows with AI Meter's own descriptions:**
    - **`wsl.exe --` failed.** It hands the arguments to a Linux shell. `wsl.exe -e` with the full path works.
    - **Pi.** A task containing shell characters reached Pi unchanged, and the answer was right.
    - **OpenCode.** It worked once `--dir .` replaced the Windows folder inside WSL.
    - **Stopping.** `taskkill /T` on `wsl.exe` ended Pi inside WSL.
  - **Tests.** 112 passed. They cover:
    - dispatch rows, and what stays unsupported;
    - the lease rules and their persistence;
    - leased room;
    - the MCP flow.

- **2026-11-02, M4 spike: OpenCode, and Windows.**
  - **Requirement.** The maintainer required delegation to work for coordinators run natively on Windows, without WSL.
  - **Install.** OpenCode 1.19.5 was installed with npm, with the maintainer's agreement, in WSL and on Windows. The maintainer added the OpenRouter key in both places.
  - **WSL and Windows behave the same:**
    - the task goes through stdin, which must be closed, otherwise OpenCode waits;
    - JSONL events: `step_start`, `text`, `tool_use`, `step_finish` (tokens), `error`;
    - exit code 1 with an `error` event on failure;
    - `--agent plan` is read-only;
    - `SIGINT` / `SIGTERM` in WSL and `taskkill /T` on Windows stop it, leaving no process.
  - **Isolation.** Without the isolation variables, OpenCode loaded 65 user skills (24,927 input tokens for "OK") and called a paid small model to title the session. With them: 9,655 tokens, and only the free model.
  - **Packaged apps.** The first Windows install landed in the Claude app's MSIX-virtualised `AppData` and was invisible to the user. Plugins must find agents in their real paths and never install them; M6 checks this from AI Meter's Store package.
  - **WSL.** npm-installed agents are on the PATH only in a login shell.

- **2026-11-02, M4 agent spike.**
  - **Pi 1.4.0, live in WSL, on OpenRouter free models.**
    - `pi -p --mode json` takes the task on stdin and writes JSONL events: `session` … `agent_settled`.
    - The outcome is in `stopReason`. The exit code was 0 for a provider error.
    - One `turn_end` per request. The read task made 3 requests and used about 4.8k tokens, about 4.6k of them system prompt.
    - `SIGINT` and `SIGTERM` stop it at once with no final event, exit codes 130 and 143, and leave no process behind.
    - RPC `abort` answers in 0.2 s with `aborted`. Tokens are 0 in both cases.
    - The first try on `google/gemma-4-31b-it:free` fell from a `429` to the user's BYOK Google key, which answered `402` with credits depleted. So a `:free` model can be billed to the user's own provider.
    - `pi auth check --json` reports readiness without the key.
  - **Codex 0.163.0.** Its `codex exec` flags are verified (stdin, `--json`, `-s`, `-C`, `--ephemeral`, `-c`, `--oss`). No free-model run, because no key is exported for it and AI Meter passes none.
  - **OpenCode.** Its documented flags are recorded; it is not installed here.
  - Details in [spike-agents.md](spike-agents.md). No code yet.

- **2026-11-02, delegation through coordinator plugins.**
  - The maintainer chose plugins over a runner owned by AI Meter. [ADR 0002](../adr/0002-delegation-through-coordinator-plugins.md) supersedes ADR 0001 decisions 2, 5, 6 and 7.
  - **AI Meter:** chooses (`list_candidates`), describes how to run the chosen agent (`describe_dispatch`: data, no shell string, no task text) and keeps cooperative capacity leases (`claim_capacity`, `renew_capacity`, `release_capacity`). The leases feed the `observed` counters.
  - **Coordinator plugins in `plugins/`:** run the agent in the background and own status, result, cancellation and worktrees.
  - **Updated:** the plan (M4 and M5, the MVP, the test plan, release), the contracts (section 3 replaced), CONTRIBUTING, SECURITY and code comments.

- **2026-11-02, M3 made agent-independent; coordinator plugins read.**
  - The maintainer asked that delegation depend on no particular agent. M3's runtime scan became a table of supported agents (`Candidates.Kinds`): Pi, OpenCode and Crush, found by their folders plus their installed program, and counted with their own keys only. A first live scan reported `opencode@wsl` from a leftover configuration folder; requiring the program fixed that, and the scan now finds only `pi@wsl` here. `list_candidates` reports `runtimes.found`. The ADR (decision 4), the plan, the contracts, CONTRIBUTING and docs/MCP.md were amended.
  - New test: two agents with keys are separate candidates in a fixed order, sharing one pool. 106 passed.
  - The coordinator plugins were read at pinned commits (plan, *Spikes*). Claude Code → Codex (official) drives `codex app-server` through an intermediary Sonnet subagent. Codex → Claude Code (community only) runs `claude -p` directly.
  - **Known flaky test.** "A client that disconnects abruptly leaves the MCP pipe serving" timed out once in three runs; it predates M3.
    - Hypothesis: after `IOException` the pipe host sleeps 5 s before creating a new instance, the same as the test's connect timeout.
    - To fix separately.

- **2026-11-02, M3 in progress.**
  - **Selection.** `src/Candidates.cs` selects execution candidates: model, runtime and credential.
    - Filters: price policy (`free_only` or `free_or_unconfirmed`), tool calls, context and output minimums, Pi holding its own key for the service.
    - Exclusions: the service's `429` backoff and used-up reported counters.
    - Order: known room first, by the remaining share of the pool's tightest known limit; then unknown room, one task at a time; ties by ids, so the result is deterministic.
    - Exclusions are grouped with reasons and `until`.
  - **MCP.** `list_candidates` returns `candidates`, `pools` (each pool once, never added up), `excluded`, `runtimes` and `freshness`. Headroom in `list_free_models` now uses the same `Candidates.Room`.
  - **Credentials.** `KeyFinder.FindAll` and `SearchAll` list every key with its app and whether it is in WSL. This covers code point 7: several keys, and where each came from.
  - **Pi.** It is found by its `~/.pi/agent` folder. Only its own keys count, and its environment: process variables on Windows, profile exports in WSL.
  - **Credential ids.** A salted, truncated SHA-256 (`cred_…`). The same key gives the same id within one run; the salt changes at every start.
  - **Tests.** Five new:
    - order and price policy;
    - task needs, shared pools, a used-up counter, `429` backoff, missing runtime and key discovery off;
    - deterministic order regardless of input order;
    - Pi taking only its own keys, and credential ids;
    - the MCP answer, which never contains a key.
  - **Live on this PC.** Pi is set up in WSL with its own keys for OpenRouter and Experiential Labs. It has none for NVIDIA or Gemini.
  - **Not run:** a live `list_candidates` call through the relay, because OpenRouter and Experiential Labs are turned off in this PC's settings, so they have no catalogue.
  - **Deferred:** declaring a key free-tier in Settings (a `configured` free status), and a paid cost policy.

- **2026-11-02, M2 in progress.**
  - **Research.** A research agent read the official documentation and made keyless calls, with each fact sourced. What it found:
    - NVIDIA's model list is public, gives ids only and is the whole catalogue, so it is not proof of access. NVIDIA documents no limits and no usage endpoint; the commonly quoted 40 RPM is not official.
    - Gemini's list needs the key in `x-goog-api-key` and gives input and output token limits. Limits apply per project and per model, and the free-tier table is no longer published.
    - OpenRouter documents 20 RPM for free models, and its key endpoint reports the daily free requests.
    - Experiential Labs documents 10 RPM and 250k TPM per model, plus 1M tokens a day shared, resetting at 00:00 UTC.
  - **Catalogue.** It moved from `Usage` to `ProviderState.Catalogue`, so a service without a usage endpoint can list its models. Code point 3 in the baseline table is resolved.
  - **Models.**
    - New list formats: `openai` (NVIDIA) and `gemini`.
    - `FreeModel` gained its output limit, a `FreeStatus` (`confirmed_free` only from a stated zero price; NVIDIA and Gemini stay `unknown`) and a `FeaturesUnknown` flag.
  - **Limits.** `RateLimit` and the catalogue's pool come from the definition. Values are documented numbers or paths into the usage answer (`provider_reported`). Daily resets follow their time zone, with daylight saving, through `Zones`.
  - **MCP.** `list_free_models` returns `free`, `limits` (`per_request`, `pool`, `rates`, `headroom`) and the new filters `min_output_tokens` and `include_unconfirmed`.
  - **Panel.** A list without prices is shown as "models, price not stated", never as free; the strings are translated into five languages.
  - **Definitions.** New: `providers/nvidia.json` (aliases `nvidia` and `nvidia-nim`) and `providers/gemini.json` (aliases `google` and `gemini`). `openrouter.json` and `experientiallabs.json` gained `pool` and `limits`.
  - **Tests.** Key discovery had no tests before; it now has fixtures for:
    - Pi aliases;
    - OpenCode `{env:}` resolved from the profile;
    - variables in the profile;
    - `!command` values, malformed JSON and placeholders, all ignored.

    There are also tests for the NVIDIA and Gemini parsers, the definition limits, daylight-saving resets in Los Angeles, and the MCP output and headroom.
  - **Live parser check on the real public lists.** NVIDIA: 69 chat models out of 85. OpenRouter: 14 free models with their output limits.
  - **NOT RUN:** reading NVIDIA and Gemini with real keys (they need the services turned on in the user's settings), and a live `429`.
  - **Moved to M3:**
    - several keys for one service and their provenance (`CredentialReference`), code point 7;
    - JSONC configuration files, which are still not read; the tests show they are skipped safely.

- **2026-11-02, M1 ready for review.**
  - The maintainer deferred the Store package relay check to M6, with packaging, and asked to continue towards the complete system.
  - A trial merge of #18, #19 (Antigravity CLI in AI apps) and #20 (copy confirmation) on `main` had no conflicts. On the combined tree, `tests/run.ps1` passed 97 tests and the build compiled.
  - Recommended merge order: #18, #19, #20, each squash-merged.
  - M2 starts on a branch stacked on `feat/mcp-freshness`. It is rebased onto `main` once #18 is merged.

- **2026-11-02, M1 live check with Antigravity CLI.** The maintainer pointed out that Gemini CLI no longer exists: Antigravity CLI (`agy`) replaces it.
  - **Version.** `agy` 1.4.12 is installed in WSL only.
  - **Where it stores MCP servers.** With the maintainer's approval, `ai-meter` was added temporarily with `agy mcp add`. The command wrote `~/.gemini/config/mcp_config.json`, using the standard `mcpServers` format plus `"disabled": false`.
  - **Permissions.** Headless runs need an allow rule for MCP tools. It was given for the session only, with `--settings '{"permissions":{"allow":["mcp(...)"]}}'`.
  - **Result against the M1 build.** `get_limits` with `max_age_seconds: 30` gave `refreshed` for Claude, ChatGPT and Antigravity. The run used 24,040 tokens.
  - **Clean-up.** `ai-meter` was removed again and `mcp_config.json` restored byte for byte. The schema cache that `agy` wrote to `~/.gemini/antigravity-cli/mcp/ai-meter` was deleted. The installed 0.8.6 was restarted.
  - **Consequences for AI apps, to fix in a separate PR:**
    - AI apps still offers "Gemini CLI". It detects it from `~/.gemini`, which exists because of `agy`, so the detection is false.
    - It would write `~/.gemini/settings.json`, which `agy` does not read.
    - It copies a `gemini mcp add` command that no longer works.
    - Sessions are still looked for in `~/.gemini/tmp`, while `agy` keeps them in `~/.gemini/antigravity-cli/conversations`.

- **2026-11-02, M1 live check.**
  - **Setup.** Build from `feat/mcp-freshness` on Windows 11.
    - Run through `AIMeter.ps1`, because Smart App Control blocks the unsigned `bin\AIMeter.exe`.
    - The installed 0.8.6 copy was stopped for the test, then restarted.
  - **Relay from a Windows process** (JSON-RPC over `mcp.ps1`):
    - `tools/list` shows the new arguments.
    - Readings younger than a minute come back `cached`.
    - After 75 s, `max_age_seconds: 60` gives `refreshed` in 3–4 s.
    - Two concurrent clients received the same snapshot.
    - A negative age is refused.
    - `list_sessions` with `refresh` gives `refreshed`.
  - **Bug found and fixed.** The first live run showed that a call right after a refresh still read `Controller.Last`, which the UI updates only after history is recorded. It answered `too_soon` with the old reading.
    - Fix: the MCP now reads the newest of `Last` and the snapshot the worker published (`RefreshGate.Newest`).
    - Added a regression test. 95 tests pass.
    - The live re-run shows `cached` with the new reading.
  - **Claude Code 2.1.316 in WSL** (`--mcp-config`, `--strict-mcp-config`, user configuration unchanged): `get_limits` called through the Windows relay over WSL interop. It returned `freshness` with `too_soon` and `retry_after`, which is correct because the last request was under a minute earlier.
  - **Codex 0.163.0 in WSL** (`-c mcp_servers.ai-meter...`, user configuration unchanged): `get_limits` with `max_age_seconds: 30` gave `refreshed` for Claude, ChatGPT and Antigravity.
  - **Settings → AI apps** in this build lists 9 apps:
    - on Windows: Claude Desktop, VS Code, Claude Code, Codex;
    - in WSL: Claude Code, Codex, Gemini CLI, OpenCode, Pi, with the adapter note.

    No switch was turned on, to leave user files unchanged.
  - **NOT RUN:**
    - Gemini CLI (not installed on Windows or in WSL);
    - Windows-native CLIs (none on PATH);
    - Claude Desktop (needs its UI);
    - the Store package relay path (needs the MSIX installed).
  - **Separate finding, not caused by M1.** Before the test, `%APPDATA%\AIMeter\mcp.ps1` (written 10:56) named `$launch = 'C:\Windows\system32'`, which is what an empty launch command produces, and had LF line endings. A relay like that cannot start AI Meter when it is not running. Both this build and the installed 0.8.6, started through `conhost`, rewrote it correctly. The origin is not identified; a copy was kept for investigation.

- **2026-11-02, M1 in progress.**
  - Implemented `refresh` and `max_age_seconds` on the MCP read tools, plus the per-service `freshness` map.
  - Added `RefreshGate` (single flight through the existing collection worker, 60 s minimum interval, 20 s timeout) and `NetworkBackoff` (`429` / `Retry-After`) for Claude, Copilot, Antigravity and defined services.
  - Separated catalogue and usage invalidation.
  - `suggest_service` now reports unknown room as `null`.
  - Tests on the working tree based on `f051b41`: `tests/run.ps1` passed 94, including 5 new tests for:
    - targets and freshness;
    - shared collection;
    - Retry-After, minimum interval, pause and timeout;
    - unknown room;
    - Retry-After parsing.
  - `build.ps1 -NoInstall` compiled.
  - Not covered by automated tests:
    - the real controller worker loop. The gate is tested with a fake worker;
    - `NetworkBackoff.Note` on a real HTTP `429`. Only the parsing is tested.
  - **NOT RUN** (M1 exit gates still open):
    - real clients (Claude Code, Codex, Gemini CLI) on Windows and in WSL, against this build;
    - the Settings → AI apps page;
    - the relay path in the Store package.

    These need the installed AI Meter replaced by this build, and the maintainer's go-ahead.

- **2026-11-02, M0 ready for review.**
  - Opened PR #16 and epic #17.
  - CI `build` passed on both commits of the PR.
  - Next action: the maintainer reviews and approves PR #16, then squash-merges it. After that, M1 starts on `feat/mcp-freshness` from the new `main`.

- **2026-11-02, M0 (scope addition).** The maintainer asked for free-model limits to be part of the contract from the start.
  - Added contracts §2.3: per-request caps, rates, concurrency, shared pools, sources, coverage and headroom.
  - Added the `list_candidates` read tool to M3.
  - Extended the M2 and M3 exit criteria and the test plan.
  - Baseline: in 0.8.6 `FreeModel` has only the context window, tool and reasoning support, and expiry (`FreeModels.cs:10-16`). It has no output cap and no rate limits.

- **2026-11-02, M0.** Reconciled the baseline above. Re-checked the code facts. Wrote ADR 0001, the plan and the draft contracts. Updated CONTRIBUTING and SECURITY. Ran `powershell -NoProfile -Sta -ExecutionPolicy Bypass -File tests/run.ps1` on `b39d449`: 89 passed, exit code 0. The M0 branch changes documentation only. Build and packaging were not run, because no code changed.
