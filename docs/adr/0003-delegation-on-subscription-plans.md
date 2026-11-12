# ADR 0003: Delegation on subscription plans

- **Status:** proposed. Merging its pull request accepts it.
- **Date:** 2026-11-12
- **Issue:** [#56](https://github.com/dichiara19/ai-meter/issues/56), part of #17
- **Widens:** [ADR 0001](0001-optional-agent-orchestration.md), whose premise is "a local executor that uses a free model". Its decisions 3 and 8 stay in force, and so does all of [ADR 0002](0002-delegation-through-coordinator-plugins.md).

## Context

Delegation runs only free models today: `list_candidates` admits models priced at zero, run by Pi or OpenCode with their own keys. Claude Code and Codex are reported as unsupported.

Many users also pay for a Claude or ChatGPT plan. AI Meter already reads the plan's 5-hour and weekly windows, and those windows often reset with room left over. That room is already paid for, but a coordinator cannot hand it any work. A coordinator in Claude Code could hand a task to Codex on the ChatGPT plan, and one in Codex to Claude Code on the Claude plan.

Spending that room safely crosses lines the free policies never came near:
- **Pay-as-you-go.** Both CLIs can run on billed API usage instead of the plan. Claude Code does so when it finds an API key in its environment or settings. Claude's extra usage continues past the plan's limits, billed. Codex can run on an API key, and on a ChatGPT plan it draws on purchased credits once the plan's limits are reached.
- **The coordinator's own plan.** A task that draws on the coordinator's plan saves nothing.
- **Accounts.** AI Meter cannot tell two accounts of one service apart, and must not try (ADR 0001 decision 2).
- **The user's other work.** The same windows serve the user's own sessions. A delegated task must not empty them.

## Decision

1. **Plan capacity is a separate consent, never a default.**
   - `list_candidates` takes `include_plan` and the plugin's `delegate` takes `allow_plan`. They sit next to `cost_policy`, which keeps governing free models, so a request can admit free models and plan capacity together.
   - Each plan also needs the user's switch in Settings › AI apps › Delegated tasks, off by default. Without it the plan's models are excluded (`plan_off_in_settings`).
   - The plugin's skill asks the user before the first plan task, as it does for best-effort and free-tier candidates.
2. **What is allowed.** Claude Code (`claude -p`) on the Claude plan, and Codex (`codex exec`) on the ChatGPT plan, through the interfaces their vendors document for scripted use, with the agent's own sign-in. The plugin runs them like any agent (ADR 0002). AI Meter still hands no credential to any agent and returns no account identity.
3. **No task draws on pay-as-you-go billing.** Each layer below blocks the cases it covers on its own:
   - **AI Meter only offers a plan it has seen signed in.** Claude: the plan windows AI Meter reads through Claude Code's sign-in. Codex: `account/read` of `codex app-server`, which AI Meter already calls, says `chatgpt` and not an API key; only that field is kept.
   - **Paid usage beyond the plan excludes the plan.** Claude with extra usage turned on is excluded (`plan_extra_usage_on`), and Codex with a credit balance or unlimited credits (`plan_credits_on`). A reserve would make billing unlikely, not impossible: AI Meter reads windows every few minutes, and parallel tasks or a per-model limit can go past it. The user turns extra usage off, or uses up the credits, to delegate.
   - **A clean environment.** The plugin starts a plan agent with only the variables a program needs to run (the system folders, the home and the PATH), never with the coordinator's environment. API keys, base URLs, cloud-provider switches and the coordinator's session variables cannot reach it. In WSL the agent starts through `env -i`.
   - **Flags that ignore the user's own configuration.** Claude Code runs with `--restricted` and `--safe-mode`, which ignore the settings files where an `apiKeyHelper` could be and the user's hooks, plugins, skills, MCP servers and `CLAUDE.md`. `--bare` is not used: it accepts only API keys. Codex runs with `--ignore-user-config` and `forced_login_method="chatgpt"`, and without the user's skills (`skills.include_instructions=false`), plugins, apps, hooks, memories, sub-agents and `AGENTS.md`.
   - **A sign-in check before starting**, in the same place and environment: `claude auth status` must report `authMethod: "claude.ai"`, `codex login status` a ChatGPT sign-in (on stderr). Only that field or line is read; the account's email and the rest are dropped. Anything else skips the candidate before its lease.
   - **A watch while it runs.** The plugin stops the agent when its own events show billed usage: Claude's `apiKeySource` other than `none`, or a rate-limit event that says overage is in use; Codex's events about credits.
4. **Room and reserve.**
   - Room comes from the windows AI Meter reads: the 5-hour window, the weekly one, and the weekly window of the model when the plan has one. The tightest decides.
   - The user sets a reserve per plan, by default 40% of the 5-hour window and 25% of the weekly one. Below it the plan is excluded (`plan_reserve`) until that window resets.
   - A reading older than 15 minutes, or none, means no candidate (`plan_usage_unknown`): room is never assumed.
   - One task at a time per plan, held by a capacity lease in the pool `<service>:plan`.
5. **Order.** Free candidates come first and the plan is the fallback, with one exception: room that is about to be lost. A plan whose 5-hour window resets within the hour, or whose weekly window resets within a day, with room above the reserve, comes before free candidates. That is where using the plan saves something.
6. **The same plan as the coordinator is excluded.** The coordinator states the service it runs on (`coordinator`); the plugin derives it from its client and passes it. A plan candidate on that service is excluded (`same_plan_as_coordinator`). Without a stated coordinator every plan candidate is excluded (`coordinator_unknown`), because the rule cannot be applied. AI Meter does not try to tell accounts apart: two accounts of one service count as the same plan.
7. **The vendors' terms.** Each vendor's terms of use apply to its CLI. The plugin uses only the documented scripted interfaces, runs one task at a time per plan, and its documentation says so. AI Meter never signs in, never handles a credential and never automates a sign-in.
8. **What is reported.** `delegate_result` names the plan and the window the task drew from, with the room before the task, the reserve and the reset, and, when the task ends, the room after it. The room after includes anything else the account did meanwhile, and the report says so. Leases count these tasks in the plan's pool, so `observed` and the track record cover them. No saving in money is claimed for them (ADR 0001 decision 8): counted tokens and window shares are reported as such.

## Consequences

- **Agent definitions grow a `plan` section** for Claude Code and Codex: the service, the models, and the sign-in check. Their `dispatch` follows the same rules as Pi's and OpenCode's, with places verified one by one. Both are verified in WSL (Claude Code 2.1.322, Codex 0.163.0, live on 2026-11-12). On Windows, Claude Code had no sign-in to check with, and Codex's read-only sandbox refused to read the workspace, so both stay unverified there.
- **Models.** Claude Code offers the aliases its definition lists (`sonnet` first, then `opus`, `fable`, `haiku`); Codex the models its own cache lists for people to pick (`~/.codex/models_cache.json`), in its order.
- **The plugin reads two more event formats** (Claude Code's `stream-json`, Codex's `--json`) and builds the clean environment and the sign-in check.
- **Privacy.** AI Meter makes no new network request: the windows, the extra-usage state, the Codex credits and the Codex account type come from reads it already makes. It reads one more local file, inside Codex's own folder: the model list Codex caches. The plugin runs the agents' own `auth status` and `login status` commands and keeps only the sign-in method. The README privacy section says so.
- **The reserve is cooperative.** Like leases, it binds only tasks run through AI Meter. The user's own sessions on the same plan go on as before and can use the room below the reserve.
- **Rollback.** Turn the plan switches off in Settings, or stop passing `include_plan`. Nothing else changes.

## Alternatives considered

- **`cost_policy: included_in_plan`.** A value of `cost_policy` would either drop free models from the request or have to mean "free or plan". A separate consent combines with either free policy and reads like the free-tier one.
- **A reserve instead of excluding extra usage and credits.** Rejected: it lowers the chance of billing without ruling it out.
- **Plans before free models.** Plan models are much stronger, but every task would spend room the user may want for their own work. The user can still pin a plan candidate.
- **Named agent profiles** (`reviewer` = Codex, read-only). Useful, but a layer on top of `runtime`, `model` and `mode`, which can already be pinned. Left to a separate issue.
