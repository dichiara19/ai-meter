// What the coordinator sees of a job: a short line for lists, and the full report once it has ended.

import { join } from "node:path";

export function brief(job) {
  return {
    job_id: job.id,
    status: job.status,
    mode: job.mode,
    candidate: job.candidate || null,
    created_at: job.created_at,
    ended_at: job.ended_at || null,
    error: job.error || null,
  };
}

export function full(job, jobsFolder) {
  const report = { ...brief(job), workspace: job.workspace, client: job.client, from: job.from, timeout_seconds: job.timeout_seconds };
  if (job.summary) {
    const { answer, answer_truncated, ...counters } = job.summary;
    report.answer = answer;
    if (answer_truncated) report.answer_truncated = true;
    report.counters = counters;
  }
  if (job.warnings?.length) report.warnings = job.warnings;
  if (job.estimated) report.estimated = job.estimated;
  if (job.best_effort_available) report.best_effort_available = job.best_effort_available;
  if (job.worktree) report.worktree = job.worktree;
  if (job.snapshot?.secrets_left_out?.length) report.secret_files_left_out = job.snapshot.secrets_left_out;
  if (job.skipped?.length) report.skipped = job.skipped;
  if (job.excluded) report.excluded = job.excluded;
  if (job.runtimes) report.runtimes = job.runtimes;
  if (job.notes?.length) report.notes = job.notes;
  if (job.version) report.agent_version = job.version;
  if (job.release_error) report.release_error = job.release_error;
  if (job.status !== "queued" && job.status !== "starting") report.logs = { events: join(jobsFolder, job.id, "events.jsonl"), stderr: join(jobsFolder, job.id, "stderr.txt") };
  return report;
}

/**
 * list_candidates, without the per-model limit details the coordinator does not need to choose. runtimes keeps
 * runtimes.unsupported (from AI Meter's agent table), with a sentence to show for each agent that cannot be used.
 */
export function candidates(found) {
  const brief = (c) => ({
    service: c.service,
    model: c.model,
    runtime: c.runtime.name,
    where: c.runtime.where,
    ...(c.runtime.distro ? { distro: c.runtime.distro } : {}),
    pool: c.pool,
    ...(c.warnings?.length ? { warnings: c.warnings } : {}),
    ...(c.estimated ? { estimated: c.estimated } : {}),
  });
  return {
    candidates: found.candidates.map(brief),
    ...(found.best_effort ? { best_effort: found.best_effort.map(brief) } : {}),
    ...(found.best_effort_available ? { best_effort_available: found.best_effort_available } : {}),
    pools: found.pools,
    excluded: found.excluded,
    runtimes: found.runtimes,
  };
}
