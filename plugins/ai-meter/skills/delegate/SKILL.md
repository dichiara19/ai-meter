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

Keep it yourself when the task needs this conversation's context, a design decision, security-sensitive judgement, or secrets. Free models are weaker than you and see only the task and the folder.

## How

1. **Optionally look first.** `delegate_candidates` lists where a task can run now: candidates in AI Meter's order, each pool's room, why other models are left out, and the agents found. AI Meter orders by room, not by quality, and breaks ties alphabetically: when several models share a pool, prefer a known coding model with a large context and pass it as `model`. Pass `runtime` (and `where`) when the user prefers an agent.
2. **Start it.** Call `delegate` with:
   - `task`: complete instructions. Name the files, say what to look for and the shape of the answer. The agent sees nothing else;
   - `workspace`: the absolute path of the repository root (the server may not run in it);
   - `mode`: `read_only` (default), or `modify` for a task that changes files;
   - optionally `model`, `runtime`, `where`, `min_context`, `timeout_seconds`;
   - `wait_seconds` up to 50, so that short tasks answer in the same call.
3. **Follow it.** While `status` is `queued`, `starting` or `running`, call `delegate_result` with `wait_seconds: 50`. `delegate_status` lists recent jobs; `delegate_cancel` stops one and releases its lease.
4. **Check the answer before using it.** Verify claims against the code. A wrong but confident answer is common on small free models.
5. **For `modify`, review the worktree.** The change is in `worktree.path`, on branch `worktree.branch`, made from the last commit (uncommitted changes in the user's checkout are not in it). Nothing is merged. Read the diff (`worktree.review`), then bring the change over yourself only if the user agrees, or discard it with `worktree.discard`.
6. **Tell the user** which agent and model ran it, and the requests and tokens from `counters`.

## When it does not start

- `error` with `excluded` and `runtimes`: no free model fits, or no agent has a key of its own. Explain the reasons. `runtimes.unsupported` lists agents that are installed but cannot be used (such as Codex or Claude Code), each with a `detail` sentence you can show. Never install an agent or handle a key: the user sets agents up.
- `skipped`: candidates that were tried and why, such as AI Meter's own error for that agent, an agent and place it has not verified, or a pool with no room.
- AI Meter must be installed on Windows; its relay starts it when needed.

## Best effort: only with the user's agreement

Some services, such as NVIDIA and the Gemini API, do not state prices or tool support. Their models are never ordinary candidates. They can be **best-effort candidates** when an agent's own model list has them at no price.

- `delegate_candidates` with `include_best_effort: true` lists them in `best_effort`. Each comes with `estimated`, which says what rests on the agent's word (`unverified`: price, tools, context, limits). Without the flag, `best_effort_available` counts them, and a failed job reports it too.
- **Before using one, ask the user.** Say that the information is estimated, and that the task may fail, stop or meet limits no one has stated. Then call `delegate` with `allow_best_effort: true`. Ordinary candidates are still tried first.
- If it fails, report the agent's error and decide with the user: try another model, or do the task yourself. Do not retry the same model on your own.

## Money

- `counters.paid_request: true`: a request was priced above zero, so the plugin stopped the agent. Tell the user; do not retry on the same model.
- `warnings` containing `openrouter_byok_fallback`: OpenRouter can retry a `:free` request on a provider key the user linked to OpenRouter (BYOK), billed to that account, while the agent still shows a price of zero. Mention it once. `counters.byok_fallback: true` means OpenRouter's error named such a key: tell the user that their linked key was used, and suggest unlinking it before delegating again.

## Good to know
- The task text and the agent's output stay on this PC, in `~/.aimeter/jobs`, for a week. Never put secrets in a task.
