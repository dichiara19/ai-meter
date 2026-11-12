// What the coordinator sees of a job: a short line for lists, and the full report once it has ended. Both are meant to
// be read by a model, so they keep what helps judge the result (the evidence in activity, the complete change of a
// modify task) and leave out what the coordinator cannot act on.

import { join } from "node:path";

export function brief(job) {
  return {
    job_id: job.id,
    status: job.status,
    mode: job.mode,
    candidate: job.candidate || null,
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
  return {
    candidates: found.candidates.map(brief),
    ...(found.best_effort ? { best_effort: found.best_effort.map(brief) } : {}),
    ...(found.best_effort_available ? { best_effort_available: found.best_effort_available } : {}),
    pools,
    excluded: shortenExcluded(found.excluded),
    runtimes: found.runtimes,
  };
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
