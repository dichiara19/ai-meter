# Agent orchestration: status

The working log for the [agent orchestration plan](agent-orchestration.md). Update it at the end of every working session. Record what was actually done, not what was planned. Keep test evidence short and free of secrets.

Allowed states: `planned`, `in_progress`, `blocked`, `in_review`, `merged`, `released`.
- `merged` needs the commit on `main`.
- `released` needs a published tag, release and artefacts.

## Milestones

| Milestone | State | Branch / PR | Base / head SHA | Tests and evidence | Blockers / next action |
| --- | --- | --- | --- | --- | --- |
| M0 Baseline, ADR, contracts | in_progress | `docs/agent-orchestration-plan` | `b39d449` / not yet pushed | Documentation only. `tests/run.ps1` on the base: 89 passed | Maintainer review of [ADR 0001](../adr/0001-optional-agent-orchestration.md) and of the open decisions below. Create the epic issue |
| M1 MCP freshness | planned | `feat/mcp-freshness`, from `main` | — | — | Starts once ADR 0001 is accepted. PR #15 is already merged, so there is no parent branch to follow |
| M2 Provider discovery | planned | `feat/provider-discovery` | — | — | Needs the M1 freshness contract |
| M3 Execution candidates | planned | `feat/execution-candidates` | — | — | Needs M1 and M2 |
| M4 Pi spike and runner MVP | planned | `feat/agent-jobs` | — | — | Spike first. Real routing needs M3 |
| M5 Client delegation | planned | `feat/client-delegation` | — | — | Needs M4 |
| M6 Stabilisation and release candidate | planned | — | — | — | Needs M1–M5 |

## Baseline (verified 2026-11-02)

`gh` is not installed on the development PC, so the GitHub state below comes from the public REST API (`api.github.com/repos/dichiara19/ai-meter`) and `git fetch`.

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

1. **Accept [ADR 0001](../adr/0001-optional-agent-orchestration.md).** Merging this PR accepts it.
2. **Default refresh behaviour of read tools** ([contracts §1.1](orchestration-contracts.md#11-arguments)). The recommendation is no network unless the caller asks for it, which keeps the 0.8.6 promise. The original plan proposed refreshing by default.
3. **Credentials for the runner in the MVP.** The recommendation is that the runtime uses only its own sign-in, and AI Meter never passes a key to it (ADR 0001, decision 3).
4. **Epic issue.** It has not been created, because `gh` is unavailable and creating it publishes content. A draft is in the PR description.

## Log

- **2026-11-02, M0.** Reconciled the baseline above. Re-checked the code facts. Wrote ADR 0001, the plan and the draft contracts. Updated CONTRIBUTING and SECURITY. Ran `powershell -NoProfile -Sta -ExecutionPolicy Bypass -File tests/run.ps1` on `b39d449`: 89 passed, exit code 0. The M0 branch changes documentation only. Build and packaging were not run, because no code changed.
