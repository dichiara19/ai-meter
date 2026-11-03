// Delegated tasks as background jobs. start() records a job and hands it to a detached worker process, so the
// coordinator's call returns at once and the task survives the call. The worker (runJob) follows ADR 0002:
//   list_candidates -> describe_dispatch -> claim_capacity -> start the agent without a shell, task on stdin
//   -> read its events -> release_capacity with the outcome.
// It describes before claiming, so a lease is only taken for a candidate the plugin can actually start.
//
// Each job is a folder under the jobs folder (~/.aimeter/jobs, or AI_METER_JOBS): job.json (the state, written only
// by the worker, apart from a worker that died), task.txt, events.jsonl (the agent's output) and stderr.txt. A
// "cancel" file asks the worker to stop the agent. Folders of ended jobs are removed after a week. A read-only task
// works in a snapshot next to the jobs folder, removed when the task ends; the worktrees of tasks that changed files
// live there too and are never removed by the plugin (workspace.mjs).

import { spawn, execFileSync } from "node:child_process";
import { randomBytes } from "node:crypto";
import { createWriteStream, existsSync, mkdirSync, readFileSync, readdirSync, renameSync, rmSync, statSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { canRead, readerFor, summarise } from "./agents.mjs";
import { place, ToolError, withMeter } from "./meter.mjs";
import { createSnapshot, createWorktree, describeChanges, removeSnapshot, repositoryOf } from "./workspace.mjs";

const ENDED = new Set(["completed", "failed", "cancelled", "timed_out"]);
const KEEP_DAYS = 7;
const ANSWER_LIMIT = 20000;
export const DEFAULT_TIMEOUT_SECONDS = 1200;
export const MAXIMUM_TIMEOUT_SECONDS = 7000;   // A lease lasts at most 7200 s, so one claim covers the whole task.

export const root = () => process.env.AI_METER_JOBS || join(homedir(), ".aimeter", "jobs");
const worktrees = () => join(dirname(root()), "worktrees");
const snapshots = () => join(dirname(root()), "snapshots");
const folderOf = (id) => join(root(), id);

export function load(id) {
  if (!/^[a-z0-9-]+$/.test(id || "")) throw new Error("Unknown job: " + id);
  const file = join(folderOf(id), "job.json");
  if (!existsSync(file)) throw new Error("Unknown job: " + id);
  return JSON.parse(readFileSync(file, "utf8"));
}

function save(job) {
  const file = join(folderOf(job.id), "job.json");
  writeFileSync(file + ".tmp", JSON.stringify(job, null, 2));
  renameSync(file + ".tmp", file);
}

function alive(pid) {
  if (!pid) return false;
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return error.code === "EPERM";
  }
}

/**
 * A job as the coordinator sees it. A job whose worker died is reported as failed; its lease expires on its own.
 * A job still queued after a minute never got its worker.
 */
export function view(job) {
  if (!ENDED.has(job.status) && job.worker_pid && !alive(job.worker_pid)) {
    job = { ...job, status: "failed", error: job.error || "The worker stopped before the task ended; the lease expires on its own." };
  } else if (job.status === "queued" && Date.now() - Date.parse(job.created_at) > 60000) {
    job = { ...job, status: "failed", error: "The worker did not start." };
  }
  return job;
}

/** Record a job and start its worker. options: task, workspace, mode, client, timeout_seconds and optional pins. */
export function start(options) {
  if (!options.task || !options.task.trim()) throw new Error("task is empty.");
  if (!options.workspace || !existsSync(options.workspace)) throw new Error("workspace must be an existing folder: " + options.workspace);
  const mode = options.mode || "read_only";
  if (mode !== "read_only" && mode !== "modify") throw new Error("mode must be read_only or modify.");
  repositoryOf(options.workspace);
  const timeout = Math.min(Math.max(Number(options.timeout_seconds) || DEFAULT_TIMEOUT_SECONDS, 5), MAXIMUM_TIMEOUT_SECONDS);

  prune();
  const id = "j" + Date.now().toString(36) + randomBytes(2).toString("hex");
  mkdirSync(folderOf(id), { recursive: true });
  writeFileSync(join(folderOf(id), "task.txt"), options.task);
  const job = {
    id,
    status: "queued",
    created_at: new Date().toISOString(),
    client: options.client || "unknown",
    from: place(),
    workspace: options.workspace,
    mode,
    timeout_seconds: timeout,
    wanted: pick(options, ["service", "model", "runtime", "where", "distro", "min_context", "cost_policy"]),
    allow_best_effort: options.allow_best_effort === true || options.allow_best_effort === "true",
    skipped: [],
  };
  save(job);

  const script = join(dirname(fileURLToPath(import.meta.url)), "..", "delegate.mjs");
  // From here on only the worker writes job.json; it records its own pid.
  const worker = spawn(process.execPath, [script, "run-job", id], { detached: true, stdio: "ignore", windowsHide: true });
  worker.unref();
  return { ...job, worker_pid: worker.pid };
}

/** The latest jobs, newest first. */
export function list(limit = 20) {
  if (!existsSync(root())) return [];
  return readdirSync(root())
    .filter((name) => existsSync(join(folderOf(name), "job.json")))
    .map((name) => view(load(name)))
    .sort((a, b) => b.created_at.localeCompare(a.created_at))
    .slice(0, limit);
}

/** Wait up to waitSeconds for a job to end, then return it. */
export async function wait(id, waitSeconds = 0) {
  const deadline = Date.now() + Math.max(0, waitSeconds) * 1000;
  let job = view(load(id));
  while (!ENDED.has(job.status) && Date.now() < deadline) {
    await sleep(500);
    job = view(load(id));
  }
  return job;
}

/** Ask a job's worker to stop its agent and wait for it, up to 15 s. A job with no worker left is closed here. */
export async function cancel(id) {
  let job = view(load(id));
  if (ENDED.has(job.status)) return job;
  writeFileSync(join(folderOf(id), "cancel"), "");
  job = await wait(id, 15);
  if (ENDED.has(job.status) || alive(job.worker_pid)) return job;

  stopProcess(job.agent_pid);
  if (job.snapshot) removeSnapshot(job.snapshot);
  if (job.lease_id) await release(job.lease_id, { outcome: "cancelled" }).catch(() => {});
  job = { ...load(id), status: "cancelled", ended_at: new Date().toISOString() };
  save(job);
  return job;
}

/** The worker: run one job to its end. Every failure ends up in job.json, never as an unhandled error. */
export async function runJob(id) {
  const job = load(id);
  const task = readFileSync(join(folderOf(id), "task.txt"), "utf8");
  job.worker_pid = process.pid;
  job.status = "starting";
  save(job);

  try {
    if (!job.from) throw new Error("Delegation needs Windows or WSL, where AI Meter runs.");
    const chosen = await choose(job);
    if (!chosen) return;
    await runAgent(job, chosen, task);
  } catch (error) {
    if (job.snapshot) removeSnapshot(job.snapshot);
    job.status = "failed";
    job.error = error.message;
    job.ended_at = new Date().toISOString();
    save(job);
  }
}

/**
 * Pick the first candidate in AI Meter's order that matches what the job asked for, whose events the plugin can read,
 * that AI Meter can describe for this place and whose pool grants a lease. A candidate AI Meter refuses to describe
 * (an error such as "no key of its own", or supported: false) is skipped with the reason, not fatal to the job.
 * Best-effort candidates (built on an agent's own model list, docs/MCP.md) come only after every ordinary one, and
 * only when the job allows them. Returns { candidate, plan, lease } or null after failing the job.
 */
async function choose(job) {
  const wanted = job.wanted;
  // The folder is made only once a candidate is chosen, but describe_dispatch needs its path now.
  if (job.mode === "modify") {
    job.worktree = { path: join(worktrees(), job.id) };
    mkdirSync(worktrees(), { recursive: true });
  } else {
    job.snapshot = { path: join(snapshots(), job.id) };
  }
  const folder = (job.worktree || job.snapshot).path;

  return withMeter(async (meter) => {
    const found = await meter.call("list_candidates", {
      tools: true,
      ...pick(wanted, ["service", "min_context", "cost_policy"]),
      ...(job.allow_best_effort ? { include_best_effort: true } : {}),
    });
    const matches = (c) =>
      (!wanted.model || c.model === wanted.model) && (!wanted.runtime || c.runtime.name === wanted.runtime) && (!wanted.where || c.runtime.where === wanted.where) &&
      (!wanted.distro || c.runtime.distro === wanted.distro);
    const candidates = [
      ...found.candidates.filter(matches),
      ...(job.allow_best_effort ? (found.best_effort || []).filter(matches).map((c) => ({ ...c, bestEffort: true })) : []),
    ];
    const refusedPools = new Set();

    for (const c of candidates) {
      const which = { service: c.service, model: c.model, runtime: c.runtime.name, where: c.runtime.where, ...pick(c.runtime, ["distro"]) };
      if (refusedPools.has(c.pool)) continue;
      if (!canRead(which.runtime)) {
        job.skipped.push({ ...which, reason: "this plugin cannot read " + which.runtime + "'s output yet" });
        continue;
      }
      let plan;
      try {
        plan = await meter.call("describe_dispatch", { ...which, mode: job.mode, workspace: folder, from: job.from });
      } catch (error) {
        if (!(error instanceof ToolError)) throw error;
        job.skipped.push({ ...which, reason: error.message });
        continue;
      }
      if (!plan.supported) {
        job.skipped.push({ ...which, reason: plan.reason });
        continue;
      }
      const lease = await meter.call("claim_capacity", { service: c.service, model: c.model, runtime: c.runtime.name, client: job.client, ttl_seconds: job.timeout_seconds + 120 });
      if (lease.refused) {
        refusedPools.add(c.pool);
        job.skipped.push({ ...which, reason: "pool " + c.pool + " has no room: " + (lease.headroom?.blocking?.reason || "full") });
        continue;
      }
      job.candidate = { ...which, pool: c.pool, ...(c.bestEffort ? { best_effort: true } : {}) };
      if (c.estimated) job.estimated = c.estimated;
      job.lease_id = lease.lease_id;
      const warnings = [...new Set([...(c.warnings || []), ...(plan.warnings || [])])];
      if (warnings.length) job.warnings = warnings;
      save(job);
      return { candidate: c, plan, lease };
    }

    job.status = "failed";
    job.error = candidates.length ? "No candidate could be started; see skipped." : "No free candidate matches this task; see excluded.";
    if (!job.allow_best_effort && found.best_effort_available) job.best_effort_available = found.best_effort_available;
    job.excluded = found.excluded;
    job.runtimes = found.runtimes;
    job.ended_at = new Date().toISOString();
    save(job);
    return null;
  });
}

async function runAgent(job, chosen, task) {
  const { plan } = chosen;
  const folder = folderOf(job.id);

  try {
    if (job.worktree) job.worktree = createWorktree(job.workspace, job.id, job.worktree.path);
    else job.snapshot = createSnapshot(job.workspace, job.snapshot.path);
  } catch (error) {
    if (job.snapshot) removeSnapshot(job.snapshot);
    await release(job.lease_id, { outcome: "cancelled", requests: 0 }).catch(() => {});
    throw error;
  }
  if (job.snapshot?.secrets_left_out.length) job.warnings = [...(job.warnings || []), "secret_files_left_out"];
  if (job.worktree?.committed_secrets_visible) job.warnings = [...(job.warnings || []), "committed_secret_files_visible"];

  const reader = readerFor(plan.runtime);
  const child = spawn(plan.program, plan.arguments, {
    cwd: plan.working_directory || undefined,
    env: { ...process.env, ...plan.environment },
    stdio: ["pipe", "pipe", "pipe"],
    windowsHide: true,
  });
  job.agent_pid = child.pid;
  job.version = plan.version;
  job.notes = plan.notes;
  job.output = plan.output;
  job.status = "running";
  job.started_at = new Date().toISOString();
  save(job);

  const events = createWriteStream(join(folder, "events.jsonl"));
  const errors = createWriteStream(join(folder, "stderr.txt"));
  let pending = "";
  let stopped = null;
  const stop = (why) => {
    if (stopped) return;
    stopped = why;
    stopProcess(child.pid, child);
  };
  child.stdout.setEncoding("utf8");
  child.stdout.on("data", (chunk) => {
    events.write(chunk);
    pending += chunk;
    let index;
    while ((index = pending.indexOf("\n")) >= 0) {
      const line = pending.slice(0, index).trim();
      pending = pending.slice(index + 1);
      if (!line) continue;
      try {
        reader.feed(JSON.parse(line));
      } catch {
        // Not an event: kept in events.jsonl only.
      }
      if (reader.paid) stop("paid");   // The first request priced above zero ends the task.
    }
  });
  child.stderr.pipe(errors);
  child.stdin.on("error", () => {});   // An agent that exits early closes its stdin; the exit says why.
  child.stdin.end(task);

  const exitCode = await new Promise((resolve) => {
    const deadline = Date.now() + job.timeout_seconds * 1000;
    const timer = setInterval(() => {
      if (existsSync(join(folder, "cancel"))) stop("cancelled");
      else if (Date.now() > deadline) stop("timed_out");
    }, 500);
    child.on("error", (error) => {
      clearInterval(timer);
      reader.error = "The agent could not start: " + error.message;
      resolve(null);
    });
    child.on("exit", (code) => {
      clearInterval(timer);
      resolve(code);
    });
  });
  events.end();
  errors.end();
  if (job.snapshot) removeSnapshot(job.snapshot);

  const summary = summarise(reader, exitCode, stopped);
  if (summary.answer.length > ANSWER_LIMIT) {
    summary.answer = summary.answer.slice(0, ANSWER_LIMIT);
    summary.answer_truncated = true;
  }
  job.summary = summary;
  job.exit_code = exitCode;
  if (job.worktree) job.worktree = describeChanges(job.worktree);
  try {
    await release(job.lease_id, {
      outcome: summary.outcome,
      requests: summary.requests,
      rate_limited: summary.rate_limited,
      ...(summary.input_tokens === null ? {} : { input_tokens: summary.input_tokens, output_tokens: summary.output_tokens }),
    });
  } catch (error) {
    job.release_error = error.message;
  }
  job.status = summary.outcome;
  if (summary.outcome !== "completed") job.error = summary.error;
  job.ended_at = new Date().toISOString();
  save(job);
}

function release(leaseId, outcome) {
  return withMeter((meter) => meter.call("release_capacity", { lease_id: leaseId, ...outcome }));
}

/**
 * Stop an agent as describe_dispatch says: taskkill /T /F on Windows, which also ends an agent started in WSL through
 * wsl.exe; SIGINT elsewhere, then SIGKILL if it is still there after five seconds.
 */
function stopProcess(pid, child) {
  if (!pid || !alive(pid)) return;
  if (process.platform === "win32") {
    try {
      execFileSync("taskkill", ["/PID", String(pid), "/T", "/F"], { stdio: "ignore", windowsHide: true });
    } catch {
      // Already gone.
    }
    return;
  }
  try {
    if (child) child.kill("SIGINT");
    else process.kill(pid, "SIGINT");
  } catch {
    return;
  }
  setTimeout(() => alive(pid) && process.kill(pid, "SIGKILL"), 5000).unref();
}

/** Remove the folders of jobs that ended more than a week ago, with any snapshot a dead worker left behind. */
function prune() {
  if (!existsSync(root())) return;
  const limit = Date.now() - KEEP_DAYS * 86400000;
  for (const name of readdirSync(root())) {
    const file = join(folderOf(name), "job.json");
    try {
      if (statSync(file).mtimeMs < limit && ENDED.has(JSON.parse(readFileSync(file, "utf8")).status)) {
        rmSync(folderOf(name), { recursive: true, force: true });
        rmSync(join(snapshots(), name), { recursive: true, force: true });
      }
    } catch {
      // Not a job folder, or one being written.
    }
  }
}

function pick(source, keys) {
  const out = {};
  for (const key of keys) if (source?.[key] !== undefined && source[key] !== null && source[key] !== "") out[key] = source[key];
  return out;
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
