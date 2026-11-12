---
name: delegate
description: Hand a self-contained coding task (reading and summarising code, finding usages, drafting tests, a small mechanical fix) to a coding agent on a free model, chosen with AI Meter, to save the coordinator's own quota. Use when the user asks to delegate, offload or run something on a free model, or when a well-specified side task does not need this conversation's context.
---

# Delegate a task to a free model with AI Meter

The `ai-meter-delegate` tools run a task with a coding agent already set up on this PC (Pi or OpenCode), on a free model whose pool has room according to AI Meter. You stay the coordinator: you write the task, check the answer and decide what to keep.

## When to delegate

Good fits:
- reading code and answering a precise question, or summarising files;
- finding where something is used or defined;
- drafting tests or documentation for code you name;
- a small mechanical change in files you name.

Keep it yourself when the task needs this conversation's context, a design decision, security-sensitive judgement, or secrets. Check the free model's answer as carefully as a stranger's: it may be shallower or wrong.

## What leaves this PC

The task, and every file the agent reads, go to the provider of the chosen model. Providers of free models may log prompts or train on them. So the plugin never runs an agent in the user's folder itself:
- `read_only` works on a copy of the files Git tracks or would track, as they are on disk. Ignored files, symbolic links, submodules and files whose names suggest secrets (`.env` and its variants except `.env.example`, `.npmrc`, `*.pem`, `*.key`, private SSH keys) are left out; `secret_files_left_out` lists those it found. The copy is removed when the task ends.
- `modify` works in a new worktree made from the last commit, so ignored files such as `.env` are not in it. Committed files that look like secrets are, and `worktree.committed_secrets_visible` lists them: tell the user.
- The workspace must be in a Git repository.
- Neither is a sandbox: an agent can still read a path outside its folder. Never name such paths, or secrets, in a task.

## How

1. **Optionally look first.** `delegate_candidates` lists where a task can run now: candidates in AI Meter's order, each pool's room (and the warnings shared by all its candidates), why other models are left out, and the agents found. `excluded` names the models only where you could act on the reason; otherwise it gives a count. AI Meter orders by room, not by quality or running cost, and breaks ties alphabetically. Choose yourself when it matters, and pass `model` and `runtime`:
   - a model known to be good at code, with enough context for the files involved, and stated tool support;
   - for many small tasks, the agent with the smaller fixed cost per request: Pi sends about 4,600 tokens of system prompt with every request, OpenCode about 9,600, and both count against the same free quota;
   - the user's preferred agent (`runtime`, and `where`) when they have one.
2. **Start it.** Call `delegate` with:
   - `task`: complete instructions. Name the files, say what to look for and the shape of the answer. The agent sees nothing else;
   - `workspace`: the absolute path of the repository root (the server may not run in it); it must be in a Git repository;
   - `mode`: `read_only` (default), or `modify` for a task that changes files;
   - optionally `model`, `runtime`, `where`, `min_context`, `timeout_seconds`;
   - `wait_seconds` up to 50, so that short tasks answer in the same call.
3. **Follow it.** While `status` is `queued`, `starting` or `running`, call `delegate_result` with `wait_seconds: 50`. `delegate_status` lists recent jobs; `delegate_cancel` stops one, with the processes it started, and releases its lease. `timeout_seconds` covers the whole job, choosing the candidate included. At most four jobs run at once: a fifth is refused until one ends.
4. **Check the answer before using it.** `answer` is only the agent's own claim. A wrong but confident answer is common on small free models.
   - Check its claims against `activity`, which the plugin builds from the agent's tool events: `calls` (each tool call, its arguments in one line, and `failed`), `files_written`, and `commands` with `ok`, `exit_code` and `output_tail`, the end of the output. An answer that says tests pass needs a test command with `ok: true` and a passing result in its `output_tail`; one that says a file was changed needs that file in `files_written`. A claim with no matching call is unverified: say so, or check it yourself. No `activity` means the agent made no tool call.
   - Verify what matters against the code as well: `activity` shows that a command ran, not that it tested the right thing.
   - A failed call in `activity` is not a failed task: agents often try a command that does not match and go on.
   - `completed` means the agent ended normally; an answer cut off for length, or output the plugin could not read, is `failed` with the reason in `error`. On a completed task `error` is null; errors the agent recovered from, such as a rate limit it retried, are in `counters.recovered_errors` and need no action. Tokens are `null` when no event reported them, which is not zero.
5. **For `modify`, review the worktree.** The change is in `worktree.path`, on branch `worktree.branch`, made from the commit `worktree.base` (uncommitted changes in the user's checkout are not in it). `worktree.commits` lists commits the agent made, and `files` and `diff_stat` compare with the base, so they include them. New files are in the diff too, with status `A` (the plugin marks them intent-to-add, without staging content). Nothing is merged. Read the diff (`worktree.review`), then bring the change over yourself only if the user agrees, or discard it with `worktree.discard`.
6. **Tell the user** which agent and model ran it, how long it took (`duration_seconds`), and the requests and tokens from `counters`.

## When it does not start

- `error` with `excluded` and `runtimes`: no free model fits, or no agent has a key of its own. Explain the reasons. `runtimes.unsupported` lists agents that are installed but cannot be used (such as Codex or Claude Code), each with a `detail` sentence you can show. Never install an agent or handle a key: the user sets agents up.
- `skipped`: candidates that were tried and why, such as AI Meter's own error for that agent, an agent and place it has not verified, an agent in another WSL distribution, a key only exported in a WSL shell profile when the plugin runs on Windows, or a pool with no room.
- `excluded` with `agent_lists_price`: the service lists the model as free, but the agent's own list prices it, so the agent would pay for the first request before the plugin could stop it.
- AI Meter must be installed on Windows; its relay starts it when needed.

## Best effort: only with the user's agreement

Some services, such as NVIDIA and the Gemini API, do not state prices or tool support. Their models are never ordinary candidates. They can be **best-effort candidates** when an agent's own model list has them at no price.

- `delegate_candidates` with `include_best_effort: true` lists them in `best_effort`. Each comes with `estimated`, which says what rests on the agent's word (`unverified`: price, tools, context, limits). Without the flag, `best_effort_available` counts them, and a failed job reports it too.
- **Before using one, ask the user.** Say that the information is estimated, and that the task may fail, stop or meet limits no one has stated. Then call `delegate` with `allow_best_effort: true`. Ordinary candidates are still tried first.
- If it fails, report the agent's error and decide with the user: try another model, or do the task yourself. Do not retry the same model on your own.

## Free tier: only with the user's agreement

Cloudflare Workers AI models are priced, but free within 10,000 Neurons a day. They are never candidates unless asked for: `excluded` reports them as `free_tier_not_allowed`.

- `delegate_candidates` with `include_free_tier: true` lists them, with `warnings: ["free_tier_budget"]`.
- **Before using one, ask the user.** Say that the task stops when it reaches what is left of today's free Neurons, and that on Cloudflare's paid plan the one request that crosses the limit is billed. Then call `delegate` with `allow_free_tier: true`.
- `agent_price_unknown` means no agent with a Cloudflare key prices the model, so its spending would be invisible; `allowance_unknown` that today's use could not be read. Neither can be worked around.

## Money

- `counters.paid_request: true`: a request was priced above zero, so the plugin stopped the agent. That request was already made and is billed: tell the user, with `counters.cost`, and do not retry on the same model.
- On a free tier, `counters.allowance` is the budget in US dollars, and `paid_request: true` means the requests cost more than it: the free Neurons are used up for today. Tell the user, with `counters.cost`, and do not retry on that service today.
- `warnings` containing `openrouter_byok_fallback` (in `delegate_candidates`, on the OpenRouter pool): OpenRouter can retry a `:free` request on a provider key the user linked to OpenRouter (BYOK), billed to that account, while the agent still shows a price of zero. Mention it once. `counters.byok_fallback: true` means OpenRouter's error named such a key: tell the user that their linked key was used, and suggest unlinking it before delegating again.

## Good to know
- Copies of the task text and the agent's output are kept on this PC, in `~/.aimeter/jobs`. Folders of ended jobs are removed a week later, the next time a task starts; nothing removes them if no task starts again. The provider has its own copy (see "What leaves this PC").
