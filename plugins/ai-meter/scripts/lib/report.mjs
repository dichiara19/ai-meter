// What the coordinator sees of a job: a short line for lists, and the full report once it has ended. Both are meant to
// be read by a model, so they keep what helps judge the result (the evidence in activity, the complete change of a
// modify task) and leave out what the coordinator cannot act on.

import { join } from "node:path";

/**
 * A job in a few fields. The candidate carries managed for AI Meter's managed Pi, and shell when the plugin chose the
 * shell its commands ran through (git_bash or powershell on Windows, bash inside WSL); a task that runs no command says
 * nothing about shells.
 */
export function brief(job) {
  const shell = job.shell && job.shell !== "none" ? { shell: job.shell } : {};
  return {
    job_id: job.id,
    status: job.status,
    mode: job.mode,
    candidate: job.candidate ? { ...job.candidate, ...shell } : null,
    created_at: job.created_at,
    ended_at: job.ended_at || null,
    ...(job.ended_at ? { duration_seconds: secondsBetween(job.created_at, job.ended_at) } : {}),
    error: job.error || null,
  };
}

export function full(job, jobsFolder) {
  const report = { ...brief(job), workspace: job.workspace, client: job.client, from: job.from, timeout_seconds: job.timeout_seconds };
  if (job.coordinator) report.coordinator = job.coordinator;
  if (job.plan) report.plan = drewFrom(job);
  if (job.summary) {
    const { answer, answer_truncated, activity, plan_after, ...counters } = job.summary;
    report.answer = answer;
    if (answer_truncated) report.answer_truncated = true;
    if (activity) report.activity = activity;
    report.counters = counters;
  }
  if (job.warnings?.length) report.warnings = job.warnings;
  if (job.estimated) report.estimated = job.estimated;
  if (job.budget) report.budget = job.budget;
  if (job.best_effort_available) report.best_effort_available = job.best_effort_available;
  if (job.worktree) report.worktree = job.worktree;
  if (job.snapshot?.secrets_left_out?.length) report.secret_files_left_out = job.snapshot.secrets_left_out;
  if (job.snapshot?.left_behind) report.snapshot_left_behind = job.snapshot.path;
  if (job.skipped?.length) report.skipped = job.skipped;
  if (job.excluded) report.excluded = shortenExcluded(job.excluded);
  if (job.runtimes) report.runtimes = job.runtimes;
  // A job saves runtimes only when no candidate could run: then the user may have a step to take. So may a job that
  // ran and whose key Cloudflare refused.
  const step = job.runtimes ? nextStep(job.runtimes) : refusedKeyStep(job);
  if (step) report.next_step = step;
  if (job.notes?.length) report.notes = job.notes;
  if (job.version) report.agent_version = job.version;
  if (job.release_error) report.release_error = job.release_error;
  if (job.dropped_lines) report.dropped_lines = job.dropped_lines;
  if (job.status !== "queued" && job.status !== "starting") report.logs = { events: join(jobsFolder, job.id, "events.jsonl"), stderr: join(jobsFolder, job.id, "stderr.txt") };
  return report;
}

/**
 * Where a plan task drew from (ADR 0003): the plan, the window that bound it and every window with the room left before
 * the task (when its lease was taken), the user's reserve and the reset, and, once it ended, the room after it, from
 * the agent's own events (Claude Code) or a fresh reading by AI Meter. The room after counts everything the account did
 * meanwhile, on any app or PC, so the difference is not this task's alone.
 */
function drewFrom(job) {
  const plan = job.plan;
  return {
    service: plan.service,
    window: plan.binding_window,
    before: plan.windows,
    ...(job.plan_after ? { after: job.plan_after } : {}),
    note: "Drawn from the " + plan.service + " plan's windows, not billed. The room after includes anything else this account used meanwhile.",
  };
}

/**
 * list_candidates, without the per-model limit details the coordinator does not need to choose. Each candidate keeps
 * its context window and reply size, so the coordinator can pin a model by hand, and AI Meter's reason for its place
 * in the order (why). runtimes keeps runtimes.unsupported (from AI Meter's agent table), with a sentence to show for
 * each agent that cannot be used. A warning that every candidate of a pool carries is given once, on that pool in
 * pools, instead of on each candidate.
 */
export function candidates(found) {
  const all = [...found.candidates, ...(found.best_effort || [])];
  const pools = (found.pools || []).map((pool) => {
    const warnings = sharedWarnings(all.filter((c) => c.pool === pool.pool));
    return warnings.length ? { ...pool, warnings } : pool;
  });
  const onPool = new Map(pools.map((pool) => [pool.pool, pool.warnings || []]));
  const brief = (c) => {
    const warnings = (c.warnings || []).filter((warning) => !onPool.get(c.pool)?.includes(warning));
    return {
      service: c.service,
      model: c.model,
      runtime: c.runtime.name,
      where: c.runtime.where,
      ...(c.runtime.distro ? { distro: c.runtime.distro } : {}),
      ...(c.runtime.managed ? { managed: true } : {}),
      pool: c.pool,
      context_tokens: c.limits?.per_request?.context_tokens?.value ?? c.estimated?.context_tokens ?? null,
      max_output_tokens: c.limits?.per_request?.max_output_tokens?.value ?? c.estimated?.max_output_tokens ?? null,
      ...(c.rank?.why ? { why: c.rank.why } : {}),
      ...(c.billing ? { billing: c.billing } : {}),
      ...(c.plan ? { plan: { windows: c.plan.windows, binding_window: c.plan.binding_window, expiring: c.plan.expiring } } : {}),
      ...(warnings.length ? { warnings } : {}),
      ...(c.estimated ? { estimated: c.estimated } : {}),
    };
  };
  const step = found.candidates.length || found.best_effort?.length ? null : nextStep(found.runtimes);
  return {
    candidates: found.candidates.map(brief),
    ...(found.best_effort ? { best_effort: found.best_effort.map(brief) } : {}),
    ...(found.best_effort_available ? { best_effort_available: found.best_effort_available } : {}),
    pools,
    excluded: shortenExcluded(found.excluded),
    runtimes: found.runtimes,
    ...(step ? { next_step: step } : {}),
  };
}

const SETTINGS = "AI Meter › Settings › MCP › Delegation agent";

/**
 * The next step for the user when no candidate could run (ADR 0005 decision 5), as one short sentence for the
 * coordinator to relay, or null when runtimes show none. From AI Meter's runtimes, the first that applies:
 *   1. no agent of the user's own was found and no managed copy is installed: AI Meter can set one up (inside WSL when
 *      there is WSL, running or stopped, else on Windows);
 *   2. a managed copy is installed but holds no key: tick a service's key;
 *   3. the copy inside WSL is installed but cannot be reached: start WSL.
 * A PC without WSL or Git for Windows that has the Windows copy is not a problem (PowerShell is used): nothing is said.
 * Only the user installs or sets up anything, in AI Meter's Settings; the sentence never asks the coordinator to.
 * Nothing when this AI Meter does not offer a managed copy (no runtimes.managed).
 */
export function nextStep(runtimes) {
  const copies = Array.isArray(runtimes?.managed) ? runtimes.managed : [];
  if (!copies.length) return null;
  const installed = copies.filter((copy) => copy.installed);
  const ownAgents = (runtimes.found || []).filter((agent) => !agent.managed);
  const where = (copy) => (copy.where === "wsl" ? "inside WSL" + (copy.distro ? " (" + copy.distro + ")" : "") : "on Windows");
  const settings = (copy) => copy?.set_up_in || SETTINGS;

  if (!installed.length && !ownAgents.length) {
    const wsl = runtimes.environment?.wsl === "running" || runtimes.environment?.wsl === "stopped";
    const proposed = copies.find((copy) => copy.where === (wsl ? "wsl" : "windows")) || copies[0];
    return `No coding agent is set up on this PC for delegated tasks: the user can have AI Meter set up its own copy of Pi ${where(proposed)}, in ${settings(proposed)}.`;
  }
  const keyless = installed.find((copy) => !(copy.services || []).length);
  if (keyless) return `AI Meter's copy of Pi ${where(keyless)} holds no key yet: the user can tick a service's key for it in ${settings(keyless)}.`;
  const unreachable = installed.find((copy) => copy.where === "wsl" && copy.reachable === false);
  if (unreachable) return `AI Meter's copy of Pi ${where(unreachable)} cannot be reached: the user can start WSL${unreachable.distro ? " (" + unreachable.distro + ")" : ""}, then try again.`;
  return null;
}

/**
 * The next step when Cloudflare refused the task's model or key, or null for any other job. A Cloudflare token that
 * can read the account's usage, which is all AI Meter needs to show the neurons, cannot run a model unless it also
 * carries the Workers AI permission: Cloudflare then answers 401 or 403 with its code 10000 ("Authentication error"),
 * which reads as a wrong key although the key is right. The same answer comes for a token of another account, so the
 * sentence says "most likely". The key does not change when the token is edited, so nothing is to be done in AI Meter.
 */
export function refusedKeyStep(job) {
  if (job?.status !== "failed" || job.candidate?.service !== "cloudflare-workers-ai") return null;
  const error = String(job.error || "");
  // A model Cloudflare keeps for the Workers Paid plan (403, code 5035). AI Meter leaves out those Cloudflare's pricing
  // page names, but the page is read by hand: a newer one can still be offered, or pinned by the coordinator.
  if (/\b403\b/.test(error) && /"code"\s*:\s*5035\b/.test(error))
    return "Cloudflare offers this model only on the Workers Paid plan, and refused it at no cost. Choose another Cloudflare model, or another service; nothing is to be changed in AI Meter.";
  if (!/\b40[13]\b/.test(error) || !/"code"\s*:\s*10000\b/.test(error)) return null;
  return "Cloudflare refused the token for Workers AI: it most likely lacks the Workers AI permission, which reading the usage does not need. The user can add Account › Workers AI › Read to the token at https://dash.cloudflare.com/profile/api-tokens; the key stays the same, so nothing changes in AI Meter.";
}

/**
 * Exclusion reasons whose model names help the coordinator: it can pick one of those models after the user agrees
 * (free_tier_not_allowed), after a key is set up (runtime_has_no_key), with a smaller min_context, or explain the
 * agent's price (agent_lists_price). For the other reasons, such as price_not_confirmed or no_tools, nothing it can do
 * makes those models usable, so only their count is kept.
 */
const REASONS_WITH_MODELS = new Set([
  "free_tier_not_allowed", "runtime_has_no_key", "context_too_small", "output_too_small", "agent_lists_price",
  // A plan the user could allow, or whose room comes back at a reset.
  "plan_not_requested", "plan_off_in_settings", "plan_reserve",
]);

function shortenExcluded(excluded) {
  if (!Array.isArray(excluded)) return excluded;
  return excluded.map((group) => {
    if (REASONS_WITH_MODELS.has(group.reason) || !group.models) return group;
    const { models, ...rest } = group;
    return rest;
  });
}

/** The warnings every one of these candidates carries. */
function sharedWarnings(candidates) {
  if (!candidates.length) return [];
  return (candidates[0].warnings || []).filter((warning) => candidates.every((c) => (c.warnings || []).includes(warning)));
}

function secondsBetween(start, end) {
  const seconds = (Date.parse(end) - Date.parse(start)) / 1000;
  return Number.isFinite(seconds) ? Math.round(seconds * 10) / 10 : null;
}
