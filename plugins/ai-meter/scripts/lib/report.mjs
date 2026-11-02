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
  if (job.worktree) report.worktree = job.worktree;
  if (job.skipped?.length) report.skipped = job.skipped;
  if (job.excluded) report.excluded = job.excluded;
  if (job.runtimes) report.runtimes = job.runtimes;
  if (job.notes?.length) report.notes = job.notes;
  if (job.version) report.agent_version = job.version;
  if (job.release_error) report.release_error = job.release_error;
  if (job.status !== "queued" && job.status !== "starting") report.logs = { events: join(jobsFolder, job.id, "events.jsonl"), stderr: join(jobsFolder, job.id, "stderr.txt") };
  return report;
}

/** list_candidates, without the per-model limit details the coordinator does not need to choose. */
export function candidates(found) {
  return {
    candidates: found.candidates.map((c) => ({ service: c.service, model: c.model, runtime: c.runtime.name, where: c.runtime.where, pool: c.pool })),
    pools: found.pools,
    excluded: found.excluded,
    runtimes: found.runtimes,
  };
}
