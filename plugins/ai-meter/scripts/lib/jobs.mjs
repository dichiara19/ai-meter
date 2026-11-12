// Delegated tasks as background jobs. start() records a job and hands it to a detached worker process, so the
// coordinator's call returns at once and the task survives the call. The worker (runJob) follows ADR 0002:
//   list_candidates -> describe_dispatch -> claim_capacity -> start the agent without a shell, task on stdin
//   -> read its events -> release_capacity with the outcome.
// It describes before claiming, so a lease is only taken for a candidate the plugin can actually start.
//
// Each job is a folder under the jobs folder (~/.aimeter/jobs, or AI_METER_JOBS): job.json (the state), task.txt,
// events.jsonl (the agent's output) and stderr.txt. A "cancel" file asks the worker to stop. A read-only task works
// in a snapshot next to the jobs folder, removed when the task ends; the worktrees of tasks that changed files live
// there too and are never removed by the plugin (workspace.mjs).
//
// Who owns what:
//   - The worker writes job.json while it lives. A job whose worker died is settled by the next coordinator call
//     that looks at it (wait, cancel): its agent is stopped, its snapshot removed, its lease released, and the
//     ended state saved, so it does not stay "running" on disk.
//   - The timeout covers the whole job, from the worker's start: choosing a candidate, starting and running the
//     agent. Cancelling and the timeout abort whatever is in progress, AI Meter's requests included; every request
//     to AI Meter has its own deadline too (meter.mjs), so releasing the lease cannot hold the end of a job.
//   - The worker owns the agent's process tree: on Linux the agent leads its own process group, which is signalled
//     as a whole when it is stopped and when it exits; on Windows taskkill /T ends the tree when the agent is stopped
//     (an agent started in WSL through wsl.exe included, as describe_dispatch says). Processes a Windows agent leaves
//     behind after exiting on its own are not tracked: that would need a Job Object.
//   - Output is bounded: lines, the event log and the error output have limits, past which the plugin drops lines,
//     stops the agent or cuts the log. At most MAXIMUM_ACTIVE_JOBS jobs run at once.
// Folders of ended jobs are removed a week later, when another job starts.

import { spawn, execFileSync } from "node:child_process";
import { randomBytes } from "node:crypto";
import { createWriteStream, existsSync, mkdirSync, readFileSync, readdirSync, renameSync, rmSync, statSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { canRead, readerFor, summarise } from "./agents.mjs";
import { place, ToolError, withMeter } from "./meter.mjs";
import { checkSignIn, cleanEnvironment, coordinatorOf } from "./plan.mjs";
import { createSnapshot, createWorktree, describeChanges, removeSnapshot, repositoryOf } from "./workspace.mjs";

const ENDED = new Set(["completed", "failed", "cancelled", "timed_out"]);
const KEEP_DAYS = 7;
const ANSWER_LIMIT = 20000;
export const DEFAULT_TIMEOUT_SECONDS = 1200;
export const MAXIMUM_TIMEOUT_SECONDS = 7000;   // A lease lasts at most 7200 s, so one claim covers the whole task.
export const MAXIMUM_ACTIVE_JOBS = 4;   // Leases pace the services; this bounds the workers and agents on this PC.
const LINE_LIMIT_CHARACTERS = 4 * 1024 * 1024;
const EVENTS_LIMIT_BYTES = 64 * 1024 * 1024;
const STDERR_LIMIT_BYTES = 1024 * 1024;
const OWN_PROCESS_GROUP = process.platform !== "win32";

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

/** Why a job that has not ended has no worker any more, or null while it has one. */
function orphaned(job) {
  if (ENDED.has(job.status)) return null;
  if (job.worker_pid && !alive(job.worker_pid)) return "The worker stopped before the task ended.";
  if (job.status === "queued" && Date.now() - Date.parse(job.created_at) > 60000) return "The worker did not start.";
  return null;
}

/** A job as the coordinator sees it, without changing anything: a job without its worker is reported as failed. */
export function view(job) {
  const reason = orphaned(job);
  return reason ? { ...job, status: "failed", error: job.error || reason } : job;
}

/**
 * Close a job whose worker died: stop its agent, remove its snapshot, release its lease and save the end, as
 * endedAs ("failed", or "cancelled" when the coordinator cancelled it). Returns the job as the coordinator sees it.
 */
async function settle(id, endedAs = "failed") {
  const job = load(id);
  const reason = orphaned(job);
  if (!reason) return view(job);
  // An agent outlives its timeout only by accident; long after it, its pid may well be another process's.
  const agentCanStillRun = job.started_at && Date.now() - Date.parse(job.started_at) < (job.timeout_seconds + 600) * 1000;
  if (agentCanStillRun) stopProcess(job.agent_pid, null, true);
  if (job.snapshot) removeSnapshot(job.snapshot);
  if (job.lease_id && !job.released) {
    try {
      await release(job.lease_id, { outcome: endedAs, requests: 0 });
      job.released = true;
    } catch (error) {
      job.release_error = error.message;
    }
  }
  const ended = { ...job, status: endedAs, error: endedAs === "cancelled" ? null : job.error || reason, ended_at: new Date().toISOString() };
  // The worker may have saved its own end meanwhile; that one wins.
  if (!ENDED.has(load(id).status)) save(ended);
  return view(load(id));
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
  const active = list(Infinity).filter((job) => !ENDED.has(job.status)).length;
  if (active >= MAXIMUM_ACTIVE_JOBS) throw new Error(`${active} delegated tasks are still running, the most this plugin runs at once. Wait for one to end, or cancel one.`);

  const id = "j" + Date.now().toString(36) + randomBytes(2).toString("hex");
  mkdirSync(folderOf(id), { recursive: true });
  writeFileSync(join(folderOf(id), "task.txt"), options.task);
  const from = place();
  const job = {
    id,
    status: "queued",
    created_at: new Date().toISOString(),
    client: options.client || "unknown",
    from,
    // An agent in another WSL distribution cannot be started from this one (choose skips it).
    ...(from === "wsl" && process.env.WSL_DISTRO_NAME ? { from_distro: process.env.WSL_DISTRO_NAME } : {}),
    workspace: options.workspace,
    mode,
    timeout_seconds: timeout,
    wanted: pick(options, ["service", "model", "runtime", "where", "distro", "min_context", "cost_policy"]),
    allow_best_effort: options.allow_best_effort === true || options.allow_best_effort === "true",
    allow_free_tier: options.allow_free_tier === true || options.allow_free_tier === "true",
    allow_plan: options.allow_plan === true || options.allow_plan === "true",
    // The service the coordinator runs on: AI Meter never offers its own plan. Said by the coordinator, or known from
    // the client that started the plugin (Claude Code, Codex).
    coordinator: options.coordinator || coordinatorOf(options.client) || null,
    skipped: [],
  };
  save(job);

  const script = join(dirname(fileURLToPath(import.meta.url)), "..", "delegate.mjs");
  // From here on the worker writes job.json; it records its own pid.
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

/** Wait up to waitSeconds for a job to end, then return it. A job whose worker died is settled here. */
export async function wait(id, waitSeconds = 0, endedAs = "failed") {
  const deadline = Date.now() + Math.max(0, waitSeconds) * 1000;
  let job = await settle(id, endedAs);
  while (!ENDED.has(job.status) && Date.now() < deadline) {
    await sleep(500);
    job = await settle(id, endedAs);
  }
  return job;
}

/**
 * Ask a job's worker to stop and wait for it, up to 15 s. A job with no worker left is closed here, as cancelled.
 * A worker still alive after 15 s is left to end on its own; the job is returned as it is.
 */
export async function cancel(id) {
  const job = await settle(id, "cancelled");
  if (ENDED.has(job.status)) return job;
  writeFileSync(join(folderOf(id), "cancel"), "");
  return wait(id, 15, "cancelled");
}

/** Why the worker stopped a job: the reason travels as an AbortSignal's reason. */
class Stopped extends Error {
  constructor(why) {
    super(why === "cancelled" ? "Cancelled." : "Timed out.");
    this.why = why;
  }
}

/** The worker: run one job to its end. Every failure ends up in job.json, never as an unhandled error. */
export async function runJob(id) {
  const job = load(id);
  const task = readFileSync(join(folderOf(id), "task.txt"), "utf8");
  job.worker_pid = process.pid;
  job.status = "starting";
  save(job);

  const deadline = Date.now() + job.timeout_seconds * 1000;
  const control = new AbortController();
  const watch = setInterval(() => {
    if (existsSync(join(folderOf(id), "cancel"))) control.abort(new Stopped("cancelled"));
    else if (Date.now() > deadline) control.abort(new Stopped("timed_out"));
  }, 500);

  try {
    if (!job.from) throw new Error("Delegation needs Windows or WSL, where AI Meter runs.");
    const chosen = await choose(job, control.signal);
    if (!chosen) return;
    await runAgent(job, chosen, task, control.signal);
  } catch (error) {
    const why = control.signal.aborted ? control.signal.reason.why : null;
    if (job.snapshot) removeSnapshot(job.snapshot);
    if (job.lease_id && !job.released) {
      try {
        await release(job.lease_id, { outcome: why || "failed", requests: 0 });
        job.released = true;
      } catch (releaseError) {
        job.release_error = releaseError.message;
      }
    }
    job.status = why || "failed";
    job.error = why === "cancelled" ? null : why === "timed_out" ? "Timed out before the agent started." : error.message;
    job.ended_at = new Date().toISOString();
    save(job);
  } finally {
    clearInterval(watch);
  }
}

/**
 * Pick the first candidate in AI Meter's order that matches what the job asked for, whose events the plugin can read,
 * that this place can start, that AI Meter can describe for this place and whose pool grants a lease. A candidate
 * AI Meter refuses to describe (an error such as "no key of its own", or supported: false) is skipped with the
 * reason, not fatal to the job. Best-effort candidates (built on an agent's own model list, docs/MCP.md) come only
 * after every ordinary one, and only when the job allows them. Returns { candidate, plan, lease } or null after
 * failing the job; throws the signal's reason when the job is cancelled or times out meanwhile.
 */
async function choose(job, signal) {
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
      // For the same model, AI Meter puts the agent in this place first: it starts without the wsl.exe hop.
      from: job.from,
      ...pick(wanted, ["service", "min_context", "cost_policy"]),
      ...(job.allow_best_effort ? { include_best_effort: true } : {}),
      ...(job.allow_free_tier ? { include_free_tier: true } : {}),
      ...(job.allow_plan ? { include_plan: true } : {}),
      ...(job.coordinator ? { coordinator: job.coordinator } : {}),
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
      if (signal.aborted) throw signal.reason;
      const which = { service: c.service, model: c.model, runtime: c.runtime.name, where: c.runtime.where, ...pick(c.runtime, ["distro"]) };
      if (refusedPools.has(c.pool)) continue;
      if (!canRead(which.runtime)) {
        job.skipped.push({ ...which, reason: "this plugin cannot read " + which.runtime + "'s output yet" });
        continue;
      }
      // describe_dispatch from WSL gives the agent's Linux path, which means something only in its own distribution.
      if (job.from_distro && which.where === "wsl" && which.distro && which.distro !== job.from_distro) {
        job.skipped.push({ ...which, reason: "it is in WSL distribution " + which.distro + ", and this plugin runs in " + job.from_distro + "; delegation does not cross distributions" });
        continue;
      }
      const onPlan = c.billing === "included_in_plan";
      const coordinator = onPlan && job.coordinator ? { coordinator: job.coordinator } : {};
      let plan;
      try {
        plan = await meter.call("describe_dispatch", { ...which, mode: job.mode, workspace: folder, from: job.from, ...coordinator });
      } catch (error) {
        if (!(error instanceof ToolError)) throw error;
        job.skipped.push({ ...which, reason: error.message });
        continue;
      }
      if (!plan.supported) {
        job.skipped.push({ ...which, reason: plan.reason });
        continue;
      }
      // A plan agent must be signed in to its plan, checked with the environment it will start with (ADR 0003).
      if (onPlan || plan.billing === "included_in_plan") {
        if (plan.environment_policy !== "clean") {
          job.skipped.push({ ...which, reason: "AI Meter did not describe a clean environment for this plan agent" });
          continue;
        }
        const signedIn = await checkSignIn(plan.sign_in_check, cleanEnvironment(plan.environment));
        if (signal.aborted) throw signal.reason;
        if (!signedIn.ok) {
          job.skipped.push({ ...which, reason: signedIn.reason });
          continue;
        }
      }
      const lease = await meter.call("claim_capacity", { service: c.service, model: c.model, runtime: c.runtime.name, client: job.client, ttl_seconds: job.timeout_seconds + 120, ...coordinator });
      if (lease.refused) {
        refusedPools.add(c.pool);
        job.skipped.push({ ...which, reason: "pool " + c.pool + " has no room: " + (lease.headroom?.blocking?.reason || "full") });
        continue;
      }
      // A free-tier model is priced: without a budget to stop at, the agent's first request would end the task.
      if ((c.warnings || []).includes("free_tier_budget") && !(lease.budget?.max_cost > 0)) {
        await meter.call("release_capacity", { lease_id: lease.lease_id, outcome: "cancelled", requests: 0 });
        job.skipped.push({ ...which, reason: "AI Meter granted no budget within the free allocation" });
        continue;
      }
      if (lease.budget) job.budget = lease.budget;
      if (lease.plan) job.plan = lease.plan;   // The plan's windows when the lease was taken: where the task draws from.
      job.candidate = { ...which, pool: c.pool, ...(c.bestEffort ? { best_effort: true } : {}), ...(onPlan ? { billing: "included_in_plan" } : {}) };
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
  }, signal);
}

async function runAgent(job, chosen, task, signal) {
  const { plan } = chosen;
  const folder = folderOf(job.id);

  if (job.worktree) job.worktree = createWorktree(job.workspace, job.id, job.worktree.path);
  else job.snapshot = createSnapshot(job.workspace, job.snapshot.path);
  if (job.snapshot?.secrets_left_out.length) job.warnings = [...(job.warnings || []), "secret_files_left_out"];
  if (job.worktree?.committed_secrets_visible) job.warnings = [...(job.warnings || []), "committed_secret_files_visible"];
  if (signal.aborted) throw signal.reason;

  const reader = readerFor(plan.runtime, job.id);   // The worktree or snapshot folder is named after the job.
  reader.allowance = chosen.lease.budget?.max_cost || 0;
  const child = spawn(plan.program, plan.arguments, {
    cwd: plan.working_directory || undefined,
    // A plan agent never gets this process's environment: no API key or base URL can make it bill an API (ADR 0003).
    env: plan.environment_policy === "clean" ? cleanEnvironment(plan.environment) : { ...process.env, ...plan.environment },
    stdio: ["pipe", "pipe", "pipe"],
    windowsHide: true,
    detached: OWN_PROCESS_GROUP,   // Its own process group, so that its children can be stopped with it.
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
  let stopped = null;
  const stop = (why) => {
    if (stopped) return;
    stopped = why;
    stopProcess(child.pid, child);
  };
  const onAbort = () => stop(signal.reason.why);
  if (signal.aborted) onAbort();
  else signal.addEventListener("abort", onAbort, { once: true });

  // One event per line. A line longer than the limit is dropped up to its newline, and counted.
  let pending = "";
  let dropping = false;
  let eventBytes = 0;
  const readLine = (text) => {
    const line = text.trim();
    if (!line) return;
    try {
      reader.feed(JSON.parse(line));
    } catch {
      // Not an event: kept in events.jsonl only.
    }
    if (reader.paid) stop("paid");   // The first request priced above zero, or past a free tier's budget, ends the task.
  };
  child.stdout.setEncoding("utf8");
  child.stdout.on("data", (chunk) => {
    eventBytes += Buffer.byteLength(chunk);
    if (eventBytes > EVENTS_LIMIT_BYTES) return stop("output_limit");
    if (!events.write(chunk)) {
      child.stdout.pause();
      events.once("drain", () => child.stdout.resume());
    }
    pending += chunk;
    let index;
    while ((index = pending.indexOf("\n")) >= 0) {
      const line = pending.slice(0, index);
      pending = pending.slice(index + 1);
      if (dropping) dropping = false;
      else readLine(line);
    }
    if (pending.length > LINE_LIMIT_CHARACTERS) {
      if (!dropping) job.dropped_lines = (job.dropped_lines || 0) + 1;
      dropping = true;
      pending = "";
    }
  });
  let stderrBytes = 0;
  child.stderr.on("data", (chunk) => {
    if (stderrBytes < STDERR_LIMIT_BYTES) errors.write(chunk.subarray(0, STDERR_LIMIT_BYTES - stderrBytes));
    stderrBytes += chunk.length;
  });
  child.stdin.on("error", () => {});   // An agent that exits early closes its stdin; the exit says why.
  child.stdin.end(task);

  // "close" comes after the output streams have ended, so no event is lost. A process the agent left behind can keep
  // them open: on Linux its group is ended at exit, and on Windows the streams are closed two seconds after the exit.
  const exitCode = await new Promise((resolve) => {
    child.on("error", (error) => {
      reader.error = "The agent could not start: " + error.message;
      resolve(null);
    });
    child.on("exit", (code) => {
      endGroup(child.pid);
      setTimeout(() => {
        child.stdout.destroy();
        child.stderr.destroy();
        resolve(code);
      }, 2000);
    });
    child.on("close", (code) => resolve(code));
  });
  signal.removeEventListener("abort", onAbort);
  if (!dropping) readLine(pending);   // A last line without its newline.
  if (stderrBytes > STDERR_LIMIT_BYTES) errors.write(`\n[AI Meter: ${stderrBytes - STDERR_LIMIT_BYTES} more bytes not kept]\n`);
  await Promise.all([new Promise((resolve) => events.end(resolve)), new Promise((resolve) => errors.end(resolve))]);

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
      ...(summary.cost > 0 ? { cost: summary.cost } : {}),
      ...(summary.input_tokens === null ? {} : { input_tokens: summary.input_tokens, output_tokens: summary.output_tokens }),
    });
    job.released = true;
  } catch (error) {
    job.release_error = error.message;
  }
  // The room a plan task left: Claude Code reports it in its own events; otherwise AI Meter reads the plan again.
  if (job.candidate?.billing === "included_in_plan") job.plan_after = summary.plan_after || (await planWindowsNow(job.candidate.service));
  job.status = summary.outcome;
  if (summary.outcome !== "completed") job.error = summary.error;
  job.ended_at = new Date().toISOString();
  save(job);

  // The snapshot goes once the end is saved, so the coordinator never waits on it. On Windows a WSL agent's folder
  // stays open for some time after the agent exits (seen for over ten seconds on 2026-11-11), so the worker keeps
  // trying for a minute; what is still left then is removed when the next job starts (prune).
  if (job.snapshot && !(await removeSnapshotPatiently(job.snapshot))) {
    job.snapshot.left_behind = true;
    save(job);
  }
}

async function removeSnapshotPatiently(snapshot) {
  for (let attempt = 0; attempt < 30; attempt++) {
    if (removeSnapshot(snapshot)) return true;
    await sleep(2000);
  }
  return false;
}

/**
 * A plan's 5-hour and weekly windows as AI Meter reads them now (get_limits with a fresh reading), or null when they
 * cannot be read. They include anything else the account used meanwhile, on any app or PC. AI Meter does not read a
 * service more often than its minimum interval (Codex: every few minutes), so read_at says how new the reading is: one
 * older than the task shows the room before it.
 */
async function planWindowsNow(service) {
  try {
    const found = await withMeter((meter) => meter.call("get_limits", { service, refresh: true }));
    const entry = (found.services || []).find((s) => s.service === service || s.id === service);
    const windows = (entry?.limits || []).filter((l) => (l.kind === "5h" || l.kind === "7d") && typeof l.remaining_percent === "number");
    if (!windows.length) return null;
    return windows.map((l) => ({
      window: l.kind, remaining_percent: l.remaining_percent, ...(l.resets_at ? { resets_at: l.resets_at } : {}), source: "ai_meter", ...(entry.read_at ? { read_at: entry.read_at } : {}),
    }));
  } catch {
    return null;
  }
}

function release(leaseId, outcome) {
  return withMeter((meter) => meter.call("release_capacity", { lease_id: leaseId, ...outcome }));
}

/**
 * Stop an agent and the processes it started. On Windows, taskkill /T /F, which also ends an agent started in WSL
 * through wsl.exe. Elsewhere SIGINT to its process group, then SIGKILL to what is left five seconds later; force
 * (an agent whose worker died) sends SIGKILL at once. An agent from before process groups is signalled alone.
 */
function stopProcess(pid, child, force = false) {
  if (!pid || !alive(pid)) return;
  if (process.platform === "win32") {
    try {
      execFileSync("taskkill", ["/PID", String(pid), "/T", "/F"], { stdio: "ignore", windowsHide: true });
    } catch {
      // Already gone.
    }
    return;
  }
  signalGroup(pid, force ? "SIGKILL" : "SIGINT");
  if (!force) setTimeout(() => signalGroup(pid, "SIGKILL"), 5000).unref();
}

/**
 * After the agent exited, end anything it left in its process group (Linux; see stopProcess for Windows). Only the
 * group: the agent's own pid is free once it exited, and may already be another process's.
 */
function endGroup(pid) {
  if (!OWN_PROCESS_GROUP || !pid) return;
  try {
    process.kill(-pid, "SIGKILL");
  } catch {
    // Nothing left.
  }
}

function signalGroup(pid, signalName) {
  try {
    process.kill(-pid, signalName);
  } catch {
    try {
      process.kill(pid, signalName);
    } catch {
      // Already gone.
    }
  }
}

/**
 * Remove the snapshots that ended jobs left behind, and the folders of jobs that ended more than a week ago, or whose
 * worker died that long ago.
 */
function prune() {
  if (!existsSync(root())) return;
  const limit = Date.now() - KEEP_DAYS * 86400000;
  for (const name of readdirSync(root())) {
    const file = join(folderOf(name), "job.json");
    try {
      if (!ENDED.has(view(JSON.parse(readFileSync(file, "utf8"))).status)) continue;
      // A snapshot is a copy of the user's files: it goes as soon as its job has ended, whatever its age.
      rmSync(join(snapshots(), name), { recursive: true, force: true });
      if (statSync(file).mtimeMs < limit) rmSync(folderOf(name), { recursive: true, force: true });
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
