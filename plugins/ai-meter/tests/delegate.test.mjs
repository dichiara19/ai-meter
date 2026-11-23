// The delegation flow with a fake AI Meter and fake agents: node --test plugins/ai-meter/tests
// Each test gets its own jobs folder and meter log; the worker processes inherit the environment set here.

import assert from "node:assert/strict";
import { execFileSync, spawn } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import * as jobs from "../scripts/lib/jobs.mjs";
import * as report from "../scripts/lib/report.mjs";
import { Activity } from "../scripts/lib/activity.mjs";
import { findGitBash } from "../scripts/lib/shell.mjs";
import { looksSecret } from "../scripts/lib/workspace.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const pi = { service: "openrouter", model: "a:free", runtime: { name: "pi", where: "wsl" }, pool: "openrouter:free" };
const openCode = { service: "openrouter", model: "a:free", runtime: { name: "opencode", where: "windows" }, pool: "openrouter:free" };
const other = { service: "nvidia", model: "b", runtime: { name: "opencode", where: "windows" }, pool: "nvidia:free" };

function setUp(scenario) {
  const folder = mkdtempSync(join(tmpdir(), "ai-meter-plugin-"));
  process.env.AI_METER_JOBS = join(folder, "jobs");
  process.env.AI_METER_RELAY = JSON.stringify([process.execPath, join(here, "fixtures", "fake-meter.mjs")]);
  process.env.FAKE_METER_SCENARIO = JSON.stringify(scenario);
  process.env.FAKE_METER_LOG = join(folder, "meter.jsonl");
  // CI's Linux runner is not WSL; the place only picks describe_dispatch's "from" and how agents are stopped.
  process.env.AI_METER_PLACE = process.platform === "win32" ? "windows" : "wsl";
  // Delegation needs a Git repository; this one is apart from the jobs folder, as a user's checkout would be.
  const workspace = join(folder, "workspace");
  execFileSync("git", ["init", "-q", workspace]);
  return { folder, workspace, calls: () => (existsSync(process.env.FAKE_METER_LOG) ? readFileSync(process.env.FAKE_METER_LOG, "utf8").trim().split("\n").map((l) => JSON.parse(l)) : []) };
}

const released = (calls) => calls().find((c) => c.tool === "release_capacity")?.arguments;

/** Whether a path disappears within ms milliseconds. */
async function gone(path, ms) {
  for (const end = Date.now() + ms; Date.now() < end; ) {
    if (!existsSync(path)) return true;
    await new Promise((resolve) => setTimeout(resolve, 200));
  }
  return !existsSync(path);
}

async function waitFor(id, status) {
  for (let i = 0; i < 100; i++) {
    if (jobs.view(jobs.load(id)).status === status) return;
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error("job never reached " + status);
}

test("a read-only Pi task gets the task on stdin unchanged and releases its lease with the counters", async () => {
  const { workspace, calls } = setUp({ candidates: [pi] });
  const task = `Quote "this" & $HOME | \`that\`; 'and' %PATH%`;
  const job = await jobs.wait(jobs.start({ task, workspace, client: "test" }).id, 20);

  assert.equal(job.status, "completed");
  assert.equal(job.summary.answer, "echo: " + task);
  assert.deepEqual(job.candidate, { service: "openrouter", model: "a:free", runtime: "pi", where: "wsl", pool: "openrouter:free" });
  assert.equal(calls().find((c) => c.tool === "list_candidates").arguments.tools, true);
  assert.equal(calls().find((c) => c.tool === "list_candidates").arguments.from, process.env.AI_METER_PLACE, "AI Meter orders agents in this place first");
  assert.equal(calls().find((c) => c.tool === "claim_capacity").arguments.client, "test");
  assert.deepEqual(released(calls), { lease_id: "lease-1", outcome: "completed", requests: 1, rate_limited: 0, input_tokens: 120, output_tokens: 7 });
});

test("an OpenCode rate limit fails the task and is reported to AI Meter", async () => {
  const { workspace, calls } = setUp({ candidates: [openCode], behaviour: { opencode: "error429" } });
  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

  assert.equal(job.status, "failed");
  assert.equal(job.exit_code, 1);
  assert.match(job.summary.error, /429/);
  assert.equal(released(calls).outcome, "failed");
  assert.equal(released(calls).rate_limited, 1);
});

test("an OpenRouter error that names a BYOK provider is reported as byok_fallback, with its 429", async () => {
  const { workspace } = setUp({ candidates: [pi], behaviour: { pi: "byok" } });
  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

  assert.equal(job.status, "failed");
  assert.equal(job.summary.byok_fallback, true);
  assert.equal(job.summary.rate_limited, 1);
  assert.match(job.error, /is_byok/, "the agent's error is on the job itself");
});

test("OpenCode's answer is the text of its last step, and tokens add up across steps", async () => {
  const { workspace, calls } = setUp({ candidates: [openCode] });
  const job = await jobs.wait(jobs.start({ task: "hello", workspace }).id, 20);

  assert.equal(job.summary.answer, "echo: hello");
  assert.deepEqual(released(calls), { lease_id: "lease-1", outcome: "completed", requests: 2, rate_limited: 0, input_tokens: 120, output_tokens: 15 });
});

test("a Pi run that ends before agent_settled is a failure, although its exit code is 0", async () => {
  const { workspace } = setUp({ candidates: [pi], behaviour: { pi: "unsettled" } });
  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

  assert.equal(job.exit_code, 0);
  assert.equal(job.status, "failed");
});

test("candidates AI Meter cannot describe, and pools that refuse a lease, are skipped with the reason", async () => {
  const { workspace, calls } = setUp({ candidates: [openCode, { ...pi, pool: "openrouter:free" }, other], unsupported: ["opencode@windows"], refuse: ["openrouter:free"] });
  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

  assert.equal(job.status, "failed");
  assert.deepEqual(job.skipped.map((s) => s.service + "/" + s.runtime), ["openrouter/opencode", "openrouter/pi", "nvidia/opencode"]);
  assert.equal(job.skipped[0].reason, "not verified here");
  assert.match(job.skipped[1].reason, /openrouter:free has no room: leased/);
  assert.equal(calls().filter((c) => c.tool === "claim_capacity").length, 1, "only the described candidate is claimed");
});

test("pins narrow the candidates, and no match fails the job with AI Meter's exclusions", async () => {
  const excluded = [{ service: "openrouter", reason: "runtime_has_no_key", count: 3 }];
  const unsupported = [{ name: "codex", display_name: "Codex", where: "wsl", distro: "Ubuntu", reason: "no_dispatch", detail: "Codex is installed in WSL (Ubuntu), but delegation does not support it yet." }];
  const { workspace, calls } = setUp({ candidates: [pi, openCode], excluded, unsupported_runtimes: unsupported });
  const pinned = await jobs.wait(jobs.start({ task: "x", workspace, runtime: "opencode" }).id, 20);
  assert.equal(pinned.candidate.runtime, "opencode");

  const none = await jobs.wait(jobs.start({ task: "x", workspace, model: "missing" }).id, 20);
  assert.equal(none.status, "failed");
  assert.deepEqual(none.excluded, excluded);
  assert.deepEqual(none.runtimes.unsupported, unsupported, "the coordinator can say why an installed agent is not used");
  assert.equal(calls().filter((c) => c.tool === "claim_capacity").length, 1);
});

test("a WSL candidate's distribution goes back to describe_dispatch, and its warnings reach the report", async () => {
  const inUbuntu = { ...pi, runtime: { name: "pi", where: "wsl", distro: "Ubuntu-24.04" }, warnings: ["openrouter_byok_fallback"] };
  const { workspace, calls } = setUp({ candidates: [inUbuntu], warnings: ["openrouter_byok_fallback"] });
  const job = await jobs.wait(jobs.start({ task: "x", workspace, distro: "Ubuntu-24.04" }).id, 20);

  assert.equal(job.status, "completed");
  assert.equal(calls().find((c) => c.tool === "describe_dispatch").arguments.distro, "Ubuntu-24.04");
  assert.equal(job.candidate.distro, "Ubuntu-24.04");
  assert.deepEqual(job.warnings, ["openrouter_byok_fallback"]);
  assert.equal(job.summary.paid_request, false, "a zero price does not stop the agent");
});

test("a describe_dispatch error skips that candidate with AI Meter's message and tries the next", async () => {
  const { workspace, calls } = setUp({ candidates: [openCode, pi], errors: { "opencode@windows": "OpenCode has no key of its own for openrouter here; call list_candidates first." } });
  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

  assert.equal(job.status, "completed");
  assert.equal(job.candidate.runtime, "pi");
  assert.match(job.skipped[0].reason, /has no key of its own/);
  assert.equal(calls().filter((c) => c.tool === "claim_capacity").length, 1);
});

test("AI Meter still reading its first data is waited for, not taken as a reason to skip the candidate", async () => {
  const { workspace } = setUp({ candidates: [pi], still_reading: 1 });
  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

  assert.equal(job.status, "completed");
  assert.deepEqual(job.skipped, []);
});

test("a runtime whose events the plugin cannot read is skipped before AI Meter is asked", async () => {
  const crush = { ...openCode, runtime: { name: "crush", where: "windows" } };
  const { workspace, calls } = setUp({ candidates: [crush, pi] });
  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

  assert.equal(job.candidate.runtime, "pi");
  assert.match(job.skipped[0].reason, /cannot read crush/);
  assert.deepEqual(calls().filter((c) => c.tool === "describe_dispatch").map((c) => c.arguments.runtime), ["pi"]);
});

for (const [runtime, candidate] of [["pi", pi], ["opencode", openCode]]) {
  test(`${runtime}: the first request priced above zero stops the agent and fails the task`, async () => {
    const { workspace, calls } = setUp({ candidates: [candidate], behaviour: { [runtime]: "paid" } });
    const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

    assert.equal(job.status, "failed");
    assert.equal(job.summary.paid_request, true);
    assert.equal(job.summary.cost, 0.0021);
    assert.match(job.summary.error, /priced as paid/);
    assert.deepEqual(released(calls), { lease_id: "lease-1", outcome: "failed", requests: 1, rate_limited: 0, cost: 0.0021 });
    assert.throws(() => process.kill(job.agent_pid, 0), "the agent is gone");
  });
}

const cloudflare = { service: "cloudflare-workers-ai", model: "@cf/openai/gpt-oss-120b", runtime: { name: "pi", where: "wsl" }, pool: "cloudflare-workers-ai:neurons", warnings: ["free_tier_budget"] };
const budget = (maxCost) => ({ metric: "neurons_per_day", unit: "neurons", limit: 10000, used: 2500, reserve: 1000, spendable: maxCost / 0.000011, max_cost: maxCost, currency: "USD" });

test("free-tier candidates are asked for only when the job allows them", async () => {
  const { workspace, calls } = setUp({ candidates: [], free_tier: [cloudflare], budget: budget(0.05) });
  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

  assert.equal(job.status, "failed");
  assert.equal(calls().find((c) => c.tool === "list_candidates").arguments.include_free_tier, undefined);
  assert.equal(calls().filter((c) => c.tool === "claim_capacity").length, 0);
});

test("a free-tier task priced within its budget completes, and its cost goes back with the lease", async () => {
  const { workspace, calls } = setUp({ candidates: [], free_tier: [cloudflare], budget: budget(0.05), behaviour: { pi: "priced" } });
  const job = await jobs.wait(jobs.start({ task: "x", workspace, allow_free_tier: true }).id, 20);

  assert.equal(job.status, "completed");
  assert.equal(calls().find((c) => c.tool === "list_candidates").arguments.include_free_tier, true);
  assert.equal(job.summary.paid_request, false, "a price within the budget does not stop the agent");
  assert.equal(job.summary.allowance, 0.05);
  assert.equal(job.budget.max_cost, 0.05);
  assert.equal(released(calls).cost, 0.0021, "AI Meter counts the cost before Cloudflare reports it");
});

test("a free-tier task whose requests cost more than its budget is stopped and fails", async () => {
  const { workspace, calls } = setUp({ candidates: [], free_tier: [cloudflare], budget: budget(0.001), behaviour: { pi: "paid" } });
  const job = await jobs.wait(jobs.start({ task: "x", workspace, allow_free_tier: true }).id, 20);

  assert.equal(job.status, "failed");
  assert.equal(job.summary.paid_request, true);
  assert.match(job.summary.error, /past the free allocation's budget of 0\.001 USD/);
  assert.equal(released(calls).cost, 0.0021);
  assert.throws(() => process.kill(job.agent_pid, 0), "the agent is gone");
});

test("a free-tier lease without a budget is given back and the candidate skipped", async () => {
  // AI Meter orders candidates by room, so a free tier can come before an ordinary one.
  const { workspace, calls } = setUp({ candidates: [], free_tier: [cloudflare, pi] });
  const job = await jobs.wait(jobs.start({ task: "x", workspace, allow_free_tier: true }).id, 20);

  assert.equal(job.status, "completed");
  assert.equal(job.candidate.service, "openrouter");
  assert.match(job.skipped.find((s) => s.service === "cloudflare-workers-ai").reason, /no budget/);
  assert.deepEqual(released(calls), { lease_id: "lease-1", outcome: "cancelled", requests: 0 }, "the budgetless lease is released first");
});

const estimated = { source: "agent:pi", tools: true, context_tokens: 131072, unverified: ["price", "tools", "limits"] };
const bestEffortPi = { ...pi, service: "nvidia", model: "meta/llama-4", pool: "nvidia:account", warnings: ["best_effort"], estimated };

test("best-effort candidates are never used unless the job allows them", async () => {
  const { workspace, calls } = setUp({ candidates: [], best_effort: [bestEffortPi] });
  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

  assert.equal(job.status, "failed");
  assert.equal(job.best_effort_available, 1, "the coordinator can tell the user that consent would help");
  assert.equal(calls().find((c) => c.tool === "list_candidates").arguments.include_best_effort, undefined);
  assert.equal(calls().filter((c) => c.tool === "claim_capacity").length, 0);
});

test("with allow_best_effort, ordinary candidates come first, then best effort, marked in the report", async () => {
  const { workspace, calls } = setUp({ candidates: [openCode], best_effort: [bestEffortPi], unsupported: ["opencode@windows"] });
  const job = await jobs.wait(jobs.start({ task: "x", workspace, allow_best_effort: true }).id, 20);

  assert.equal(job.status, "completed");
  assert.equal(calls().find((c) => c.tool === "list_candidates").arguments.include_best_effort, true);
  assert.deepEqual(calls().filter((c) => c.tool === "describe_dispatch").map((c) => c.arguments.model), ["a:free", "meta/llama-4"], "the ordinary candidate is tried first");
  assert.equal(job.candidate.best_effort, true);
  assert.deepEqual(job.estimated, estimated);
  assert.ok(job.warnings.includes("best_effort"));
});

test("cancelling stops the agent and releases the lease without tokens", async () => {
  const { workspace, calls } = setUp({ candidates: [pi], behaviour: { pi: "hang" } });
  const { id } = jobs.start({ task: "x", workspace });
  await waitFor(id, "running");
  const agent = jobs.load(id).agent_pid;
  const job = await jobs.cancel(id);

  assert.equal(job.status, "cancelled");
  assert.deepEqual(released(calls), { lease_id: "lease-1", outcome: "cancelled", requests: 0, rate_limited: 0 });
  await new Promise((resolve) => setTimeout(resolve, 300));
  assert.throws(() => process.kill(agent, 0), "the agent is gone");
});

test("a task that runs past its timeout is stopped as timed_out", async () => {
  const { workspace, calls } = setUp({ candidates: [openCode], behaviour: { opencode: "hang" } });
  const job = await jobs.wait(jobs.start({ task: "x", workspace, timeout_seconds: 5 }).id, 20);

  assert.equal(job.status, "timed_out");
  assert.equal(released(calls).outcome, "timed_out");
  assert.equal(calls().find((c) => c.tool === "claim_capacity").arguments.ttl_seconds, 125);
});

test("a modify task works in a new worktree and leaves the user's checkout alone", async () => {
  const { folder } = setUp({ candidates: [openCode], behaviour: { opencode: "write" } });
  const repository = join(folder, "repo");
  const git = (...args) => execFileSync("git", ["-C", repository, ...args], { encoding: "utf8" });
  execFileSync("git", ["init", "-q", repository]);
  writeFileSync(join(repository, "a.txt"), "a\n");
  writeFileSync(join(repository, ".env"), "TOKEN=committed by mistake\n");
  git("add", ".");
  git("-c", "user.email=t@t", "-c", "user.name=t", "commit", "-qm", "init");
  writeFileSync(join(repository, "a.txt"), "uncommitted\n");

  const job = await jobs.wait(jobs.start({ task: "x", workspace: repository, mode: "modify" }).id, 20);

  assert.equal(job.status, "completed");
  assert.equal(job.worktree.branch, "ai-meter/" + job.id);
  assert.equal(job.worktree.uncommitted_changes_left_out, true);
  assert.deepEqual(job.worktree.committed_secrets_visible, [".env"], "a committed secret is in the worktree, and the report says so");
  assert.ok(job.warnings.includes("committed_secret_files_visible"));
  // A modified file's status line starts with a space, which must not cost the path its first letter.
  assert.deepEqual(job.worktree.files, [{ status: "M", path: "a.txt" }, { status: "A", path: "made-by-agent.txt" }]);
  assert.match(job.worktree.diff_stat, /a\.txt/);
  assert.match(job.worktree.diff_stat, /made-by-agent\.txt/, "a new file is in the diff, not only in files");
  assert.match(job.worktree.diff_stat, /2 files changed/);
  assert.equal(readFileSync(join(job.worktree.path, "made-by-agent.txt"), "utf8"), "modify\n");
  assert.equal(existsSync(join(repository, "made-by-agent.txt")), false);
  assert.equal(git("status", "--porcelain"), " M a.txt\n");
});

test("a task outside a Git repository is refused before anything starts, read-only too", () => {
  const { folder, calls } = setUp({ candidates: [openCode] });
  for (const mode of ["read_only", "modify"]) assert.throws(() => jobs.start({ task: "x", workspace: folder, mode }), /needs a Git repository/);
  assert.deepEqual(calls(), []);
});

test("a read-only task sees a snapshot of the files Git tracks or would track, without secrets, removed at the end", async () => {
  const { workspace, calls } = setUp({ candidates: [pi], behaviour: { pi: "list" } });
  const git = (...args) => execFileSync("git", ["-C", workspace, ...args], { encoding: "utf8" });
  const files = {
    ".gitignore": ".env\nbuild/\n",
    "a.txt": "committed\n",
    "src/b.txt": "b\n",
    ".env": "RESEND_API_KEY=ignored-secret\n",
    "build/out.txt": "ignored\n",
    ".env.example": "RESEND_API_KEY=\n",
    "config/.env.production": "RECAPTCHA_SECRET=untracked-secret\n",
    "deploy.pem": "-----BEGIN PRIVATE KEY-----\n",
  };
  for (const [path, content] of Object.entries(files)) {
    mkdirSync(dirname(join(workspace, path)), { recursive: true });
    writeFileSync(join(workspace, path), content);
  }
  git("add", ".gitignore", "a.txt", "src/b.txt", "deploy.pem");
  git("-c", "user.email=t@t", "-c", "user.name=t", "commit", "-qm", "init");
  writeFileSync(join(workspace, "a.txt"), "uncommitted\n");
  writeFileSync(join(workspace, "new.txt"), "untracked, not ignored\n");

  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

  assert.equal(job.status, "completed");
  const seen = JSON.parse(job.summary.answer);
  assert.deepEqual(Object.keys(seen).sort(), [".env.example", ".gitignore", "a.txt", "new.txt", "src/b.txt"]);
  assert.equal(seen["a.txt"], "uncommitted\n", "the snapshot has the files as they are on disk");
  assert.deepEqual(job.snapshot.secrets_left_out.sort(), ["config/.env.production", "deploy.pem"], "secrets Git would track, or tracks, are left out too");
  assert.ok(job.warnings.includes("secret_files_left_out"));
  assert.equal(calls().find((c) => c.tool === "describe_dispatch").arguments.workspace, job.snapshot.path);
  assert.ok(await gone(job.snapshot.path, 5000), "the snapshot is removed when the task ends");
});

test("a snapshot still held open when the agent exits does not stop the task from ending, and goes later", async () => {
  const { workspace } = setUp({ candidates: [pi], behaviour: { pi: "holdopen" } });
  writeFileSync(join(workspace, "a.txt"), "a\n");
  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

  assert.equal(job.status, "completed", job.error || "");
  assert.equal(job.summary.answer, "echo: x");
  assert.ok(await gone(job.snapshot.path, 15000), "the snapshot is removed once it is no longer held");
});

test("the names that suggest secrets", () => {
  for (const name of [".env", ".env.local", "config/.env.production", "prod.env", ".npmrc", "keys/server.key", "cert.pfx", "id_ed25519"]) assert.ok(looksSecret(name), name);
  for (const name of [".env.example", ".env.sample", "env.ts", "src/environment.js", "keyboard.txt", "id.txt"]) assert.ok(!looksSecret(name), name);
});

test("the MCP server lists its tools and runs a delegation", async () => {
  const { folder, workspace } = setUp({ candidates: [pi] });
  const server = spawn(process.execPath, [join(here, "..", "scripts", "mcp-server.mjs")], { stdio: ["pipe", "pipe", "inherit"] });
  const answers = [];
  let buffer = "";
  server.stdout.on("data", (chunk) => {
    buffer += chunk;
    let i;
    while ((i = buffer.indexOf("\n")) >= 0) {
      answers.push(JSON.parse(buffer.slice(0, i)));
      buffer = buffer.slice(i + 1);
    }
  });
  const send = (message) => server.stdin.write(JSON.stringify({ jsonrpc: "2.0", ...message }) + "\n");
  const answer = async (id) => {
    for (let i = 0; i < 200 && !answers.find((a) => a.id === id); i++) await new Promise((resolve) => setTimeout(resolve, 100));
    return answers.find((a) => a.id === id);
  };

  // A failed assertion must not leave the server running: node --test would wait for it.
  try {
    send({ id: 1, method: "initialize", params: { protocolVersion: "2025-06-18", clientInfo: { name: "claude-code" } } });
    send({ method: "notifications/initialized" });
    send({ id: 2, method: "tools/list" });
    send({ id: 3, method: "tools/call", params: { name: "delegate", arguments: { task: "hi", workspace, wait_seconds: 15 } } });
    send({ id: 4, method: "tools/call", params: { name: "delegate", arguments: { task: "hi", workspace: join(folder, "missing") } } });

    assert.equal((await answer(1)).result.serverInfo.name, "ai-meter-delegate");
    assert.deepEqual((await answer(2)).result.tools.map((t) => t.name), ["delegate", "delegate_result", "delegate_status", "delegate_cancel", "delegate_candidates"]);
    const delegated = (await answer(3)).result.structuredContent;
    assert.equal(delegated.status, "completed");
    assert.equal(delegated.answer, "echo: hi");
    assert.equal(delegated.client, "claude-code");
    assert.equal((await answer(4)).result.isError, true);
    assert.equal(answers.length, 4, "the notification gets no answer");
  } finally {
    server.stdin.end();
    server.kill();
  }
});

const isAlive = (pid) => {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
};

test("cancelling a job whose worker died stops its agent, releases the lease and saves the end", async () => {
  const { workspace, calls } = setUp({ candidates: [pi], behaviour: { pi: "hang" } });
  const { id } = jobs.start({ task: "x", workspace });
  await waitFor(id, "running");
  const { worker_pid: worker, agent_pid: agent } = jobs.load(id);
  process.kill(worker);   // The agent is left without its worker, as when the worker crashes.
  for (let i = 0; i < 50 && isAlive(worker); i++) await new Promise((resolve) => setTimeout(resolve, 100));
  assert.ok(isAlive(agent), "the agent outlived its worker");

  const job = await jobs.cancel(id);

  assert.equal(job.status, "cancelled");
  assert.equal(jobs.load(id).status, "cancelled", "the end is saved, not only shown");
  assert.equal(released(calls).outcome, "cancelled");
  await new Promise((resolve) => setTimeout(resolve, 500));
  assert.equal(isAlive(agent), false, "the agent is gone");
});

test("cancelling stops the processes the agent started too", async () => {
  const { folder, workspace } = setUp({ candidates: [openCode], behaviour: { opencode: "family" } });
  const pidFile = join(folder, "child.pid");
  process.env.FAKE_CHILD_PID_FILE = pidFile;
  try {
    const { id } = jobs.start({ task: "x", workspace });
    await waitFor(id, "running");
    for (let i = 0; i < 50 && !existsSync(pidFile); i++) await new Promise((resolve) => setTimeout(resolve, 100));
    const child = Number(readFileSync(pidFile, "utf8"));
    assert.ok(isAlive(child));

    assert.equal((await jobs.cancel(id)).status, "cancelled");
    await new Promise((resolve) => setTimeout(resolve, 500));
    assert.equal(isAlive(child), false, "the agent's child is gone");
  } finally {
    delete process.env.FAKE_CHILD_PID_FILE;
  }
});

test("the timeout covers choosing a candidate: a relay that never answers cannot hold a job in starting", async () => {
  const { workspace } = setUp({ candidates: [pi], silent: ["list_candidates"] });
  const started = Date.now();
  const job = await jobs.wait(jobs.start({ task: "x", workspace, timeout_seconds: 5 }).id, 20);

  assert.equal(job.status, "timed_out");
  assert.match(job.error, /before the agent started/);
  assert.ok(Date.now() - started < 12000, "it ended soon after its timeout");
});

test("cancelling while a candidate is being chosen ends the job at once", async () => {
  const { workspace } = setUp({ candidates: [pi], silent: ["list_candidates"] });
  const { id } = jobs.start({ task: "x", workspace });
  await waitFor(id, "starting");
  const started = Date.now();
  const job = await jobs.cancel(id);

  assert.equal(job.status, "cancelled");
  assert.ok(Date.now() - started < 5000);
});

test("an answer cut off for length is a failure, not a success", async () => {
  const { workspace } = setUp({ candidates: [pi], behaviour: { pi: "length" } });
  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

  assert.equal(job.status, "failed");
  assert.match(job.error, /cut off/);
});

test("OpenCode output with no event at all is not taken as a completed task with zero tokens", async () => {
  const { workspace, calls } = setUp({ candidates: [openCode], behaviour: { opencode: "silent" } });
  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

  assert.equal(job.exit_code, 0);
  assert.equal(job.status, "failed");
  assert.match(job.error, /without any step_finish/);
  assert.equal(job.summary.input_tokens, null, "unknown tokens are not zero");
  assert.equal(released(calls).input_tokens, undefined);
});

test("OpenCode that stops after a tool-calls step has not finished", async () => {
  const { workspace } = setUp({ candidates: [openCode], behaviour: { opencode: "unfinished" } });
  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

  assert.equal(job.status, "failed");
  assert.match(job.error, /tool-calls/);
});

test("an over-long line is dropped and counted, and a last event without its newline still counts", async () => {
  const { workspace } = setUp({ candidates: [pi], behaviour: { pi: "longline" } });
  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 30);

  assert.equal(job.status, "completed");
  assert.equal(job.dropped_lines, 1);
  assert.equal(job.summary.answer, "echo: x");
});

test("a modify report compares with the worktree's base, so changes the agent committed are shown", async () => {
  const { folder } = setUp({ candidates: [openCode], behaviour: { opencode: "commit" } });
  const repository = join(folder, "repo");
  const git = (...args) => execFileSync("git", ["-C", repository, ...args], { encoding: "utf8" });
  execFileSync("git", ["init", "-q", repository]);
  writeFileSync(join(repository, "a.txt"), "a\n");
  git("add", ".");
  git("-c", "user.email=t@t", "-c", "user.name=t", "commit", "-qm", "init");

  const job = await jobs.wait(jobs.start({ task: "x", workspace: repository, mode: "modify" }).id, 20);

  assert.equal(job.status, "completed");
  assert.equal(job.worktree.commits.length, 1);
  assert.match(job.worktree.commits[0], /agent change/);
  assert.deepEqual(job.worktree.files, [{ status: "M", path: "a.txt" }, { status: "A", path: "made-by-agent.txt" }]);
  assert.match(job.worktree.diff_stat, /a\.txt/);
  assert.match(job.worktree.review, new RegExp(job.worktree.base));
});

test("no more than the most active jobs run at once", async () => {
  const { workspace } = setUp({ candidates: [pi], behaviour: { pi: "hang" } });
  const ids = [];
  try {
    for (let i = 0; i < jobs.MAXIMUM_ACTIVE_JOBS; i++) ids.push(jobs.start({ task: "x", workspace }).id);
    assert.throws(() => jobs.start({ task: "x", workspace }), /still running/);
  } finally {
    for (const id of ids) await jobs.cancel(id);
  }
});

test("from WSL, an agent in another distribution is skipped: its Linux path means nothing here", async () => {
  const inDebian = { ...pi, runtime: { name: "pi", where: "wsl", distro: "Debian" } };
  const inUbuntu = { ...pi, runtime: { name: "pi", where: "wsl", distro: "Ubuntu" } };
  const { workspace, calls } = setUp({ candidates: [inDebian, inUbuntu] });
  process.env.AI_METER_PLACE = "wsl";
  process.env.WSL_DISTRO_NAME = "Ubuntu";
  try {
    const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

    assert.equal(job.status, "completed");
    assert.equal(job.candidate.distro, "Ubuntu");
    assert.match(job.skipped[0].reason, /distribution Debian.*runs in Ubuntu/);
    assert.deepEqual(calls().filter((c) => c.tool === "describe_dispatch").map((c) => c.arguments.distro), ["Ubuntu"]);
  } finally {
    delete process.env.WSL_DISTRO_NAME;
  }
});

test("the MCP server answers malformed and over-long requests with errors, and keeps serving", async () => {
  setUp({ candidates: [] });
  const server = spawn(process.execPath, [join(here, "..", "scripts", "mcp-server.mjs")], { stdio: ["pipe", "pipe", "inherit"] });
  const answers = [];
  let buffer = "";
  server.stdout.on("data", (chunk) => {
    buffer += chunk;
    let i;
    while ((i = buffer.indexOf("\n")) >= 0) {
      answers.push(JSON.parse(buffer.slice(0, i)));
      buffer = buffer.slice(i + 1);
    }
  });
  try {
    const lines = ["null", "[]", '"text"', '{"jsonrpc":"2.0","id":1}', '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":null}', "x".repeat(2 * 1024 * 1024)];
    for (const line of lines) server.stdin.write(line + "\n");
    server.stdin.write('{"jsonrpc":"2.0","id":3,"method":"tools/list"}\n');
    for (let i = 0; i < 100 && !answers.find((a) => a.id === 3); i++) await new Promise((resolve) => setTimeout(resolve, 100));

    assert.equal(server.exitCode, null, "the server is still running");
    assert.ok(answers.find((a) => a.id === 3).result.tools.length > 0);
    const errors = answers.filter((a) => a.error);
    assert.ok(errors.every((a) => a.error.code === -32600), JSON.stringify(errors));
    assert.deepEqual(errors.map((a) => a.id), [null, null, null, 1, 2, null]);
  } finally {
    server.stdin.end();
    server.kill();
  }
});

test("the plugin's version lives in its manifests only, and they agree", async () => {
  const manifest = (path) => JSON.parse(readFileSync(join(here, "..", path), "utf8"));
  const { VERSION } = await import("../scripts/lib/version.mjs");
  assert.equal(manifest("plugin.json").version, manifest(".claude-plugin/plugin.json").version);
  assert.equal(VERSION, manifest("plugin.json").version);
  for (const file of ["scripts/mcp-server.mjs", "scripts/lib/meter.mjs"]) assert.doesNotMatch(readFileSync(join(here, "..", file), "utf8"), /"\d+\.\d+\.\d+"/, file + " has a version of its own");
  for (const file of ["mcp.json", ".mcp.json"]) assert.ok(manifest(file).mcpServers["ai-meter-delegate"].args[0].endsWith("scripts/mcp-server.mjs"), file);
});

test("a modify review shows the files the agent created, without committing them", async () => {
  const { folder } = setUp({ candidates: [openCode], behaviour: { opencode: "write" } });
  const repository = join(folder, "repo");
  execFileSync("git", ["init", "-q", repository]);
  writeFileSync(join(repository, "a.txt"), "a\n");
  execFileSync("git", ["-C", repository, "add", "."]);
  execFileSync("git", ["-C", repository, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-qm", "init"]);

  const job = await jobs.wait(jobs.start({ task: "x", workspace: repository, mode: "modify" }).id, 20);
  const inWorktree = (...args) => execFileSync("git", ["-C", job.worktree.path, ...args], { encoding: "utf8" });

  assert.equal(job.status, "completed");
  const review = inWorktree("diff", job.worktree.base);
  assert.match(review, /made-by-agent\.txt/, "the review command shows the new file");
  assert.match(review, /\+modify/, "with its contents");
  assert.equal(inWorktree("rev-parse", "HEAD").trim(), job.worktree.base, "nothing was committed");
  assert.equal(inWorktree("diff", "--cached", "--name-only"), "", "no content was staged");
});

test("Pi's tool calls become a bounded activity: calls, files written and commands, with paths in its folder", async () => {
  const { workspace } = setUp({ candidates: [pi], behaviour: { pi: "tools" } });
  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);
  const shown = report.full(job, jobs.root());

  assert.equal(job.status, "completed");
  assert.equal(shown.counters.activity, undefined, "activity is a section of its own, not a counter");
  const activity = shown.activity;
  assert.equal(activity.tool_calls, 3);
  assert.equal(activity.failed_calls, 1);
  assert.deepEqual(activity.calls.map((c) => [c.tool, c.args, c.failed === true]), [
    ["bash", "cd . && ls *.json", true],
    ["write", "slug.test.js", false],
    ["bash", "cd . && node --test 2>&1", false],
  ]);
  assert.deepEqual(activity.files_written, ["slug.test.js"]);
  assert.deepEqual(activity.commands.map((c) => [c.command, c.ok, c.exit_code]), [["cd . && ls *.json", false, 2], ["cd . && node --test 2>&1", true, undefined]]);
  assert.match(activity.commands[1].output_tail, /ℹ pass 8\nℹ fail 0$/, "the end of a command's output, where its result is");
  assert.ok(activity.commands[1].output_tail.length <= 301, "only a short excerpt of the output");
  const text = JSON.stringify(activity);
  assert.doesNotMatch(text, /contents-the-report-must-not-carry/, "never the contents of a file written");
  assert.doesNotMatch(text, new RegExp(job.id), "paths are relative to the task's folder");
});

test("OpenCode's tool_use events become the same activity, with failed edits and non-zero exits", async () => {
  const { workspace } = setUp({ candidates: [openCode], behaviour: { opencode: "tools" } });
  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);
  const activity = job.summary.activity;

  assert.equal(job.status, "completed");
  assert.deepEqual(activity.calls.map((c) => [c.tool, c.args, c.failed === true]), [
    ["bash", "ls *.json", true],
    ["write", "slug.test.js", false],
    ["edit", "slug.js", true],
    ["bash", "node --test", false],
    ["read", "", false],
  ]);
  assert.equal(activity.calls[2].error, "oldString not found in content");
  assert.deepEqual(activity.files_written, ["slug.test.js"], "a failed edit changed nothing");
  assert.deepEqual(activity.commands.map((c) => [c.ok, c.exit_code]), [[false, 2], [true, 0]]);
  assert.doesNotMatch(JSON.stringify(activity), /contents-the-report-must-not-carry/);
});

test("activity keeps the last calls and commands, and counts the earlier ones", async () => {
  const { workspace } = setUp({ candidates: [pi], behaviour: { pi: "manytools" } });
  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);
  const activity = job.summary.activity;

  assert.equal(activity.tool_calls, 45);
  assert.equal(activity.calls.length, 30);
  assert.equal(activity.earlier_calls_left_out, 15);
  assert.equal(activity.calls.at(-1).args, "echo 44", "the latest calls are the ones kept");
  assert.equal(activity.commands.length, 10);
  assert.equal(activity.earlier_commands_left_out, 35);
});

test("a run without tool calls has no activity", async () => {
  const { workspace } = setUp({ candidates: [pi] });
  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);
  assert.equal(job.summary.activity, null);
  assert.equal(report.full(job, jobs.root()).activity, undefined);
});

test("errors the agent recovered from are reported apart, so a completed task shows no error", async () => {
  const { workspace, calls } = setUp({ candidates: [pi], behaviour: { pi: "retried" } });
  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);
  const shown = report.full(job, jobs.root());

  assert.equal(shown.status, "completed");
  assert.equal(shown.error, null);
  assert.equal(shown.counters.error, null);
  assert.deepEqual(shown.counters.recovered_errors, { count: 1, last: "429: a:free is temporarily rate-limited upstream. Please retry shortly." });
  assert.equal(shown.counters.rate_limited, 1, "the rate limit still counts, and goes back to AI Meter");
  assert.equal(released(calls).rate_limited, 1);
});

test("a failed task keeps its full error, and has no recovered_errors", async () => {
  const { workspace } = setUp({ candidates: [pi], behaviour: { pi: "error429" } });
  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

  assert.equal(job.status, "failed");
  assert.match(job.summary.error, /429 Too Many Requests/);
  assert.equal(job.summary.recovered_errors, undefined);
});

test("the report gives the job's duration once it has ended", async () => {
  const { workspace } = setUp({ candidates: [pi] });
  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);
  const shown = report.full(job, jobs.root());

  assert.equal(shown.duration_seconds, Math.round((Date.parse(job.ended_at) - Date.parse(job.created_at)) / 100) / 10);
  assert.ok(shown.duration_seconds > 0);
  assert.equal(report.brief({ ...job, ended_at: undefined }).duration_seconds, undefined, "a job still running has none");
});

test("delegate_candidates gives a warning shared by a whole pool once, and drops model lists the coordinator cannot use", () => {
  const byok = ["openrouter_byok_fallback"];
  const found = {
    candidates: [{ ...pi, warnings: byok }, { ...openCode, warnings: [...byok, "other"] }, { ...other }],
    pools: [{ pool: "openrouter:free", models: 1 }, { pool: "nvidia:free", models: 1 }],
    excluded: [
      { service: "nvidia", reason: "price_not_confirmed", count: 68, models: ["m1", "m2"] },
      { service: "cloudflare-workers-ai", reason: "free_tier_not_allowed", count: 2, models: ["@cf/a", "@cf/b"] },
      { service: "openrouter", reason: "rate_limited", count: 1, models: ["x"], until: "2026-11-12T10:00:00Z" },
    ],
    runtimes: { found: [], unsupported: [] },
  };
  const shown = report.candidates(found);

  assert.deepEqual(shown.pools[0].warnings, byok);
  assert.equal(shown.pools[1].warnings, undefined);
  assert.equal(shown.candidates[0].warnings, undefined, "the pool's warning is not repeated");
  assert.deepEqual(shown.candidates[1].warnings, ["other"], "a warning of one candidate stays on it");
  assert.deepEqual(shown.excluded[0], { service: "nvidia", reason: "price_not_confirmed", count: 68 });
  assert.deepEqual(shown.excluded[1].models, ["@cf/a", "@cf/b"], "models the user can agree to use stay listed");
  assert.deepEqual(shown.excluded[2], { service: "openrouter", reason: "rate_limited", count: 1, until: "2026-11-12T10:00:00Z" });
});

test("delegate_candidates keeps each candidate's context, reply size and reason for its place", () => {
  const rank = { why: "Not tried here yet; 31B weights (Hugging Face); 262,144 context.", preference: "neutral", track_record: { score: 0 } };
  const limits = { per_request: { context_tokens: { value: 262144, source: "catalogue" }, max_output_tokens: { value: 32768, source: "catalogue" } }, rates: [{ metric: "requests_per_day" }] };
  const unknown = { per_request: { context_tokens: { value: null, source: "unknown" }, max_output_tokens: { value: null, source: "unknown" } } };
  const shown = report.candidates({
    candidates: [{ ...pi, limits, rank }],
    best_effort: [{ ...other, limits: unknown, estimated: { context_tokens: 131072, max_output_tokens: 16384 } }],
    pools: [],
    excluded: [],
    runtimes: { found: [], unsupported: [] },
  });

  const first = shown.candidates[0];
  assert.equal(first.context_tokens, 262144);
  assert.equal(first.max_output_tokens, 32768);
  assert.equal(first.why, rank.why);
  assert.equal(first.limits, undefined, "rates stay in AI Meter's answer");
  assert.equal(first.rank, undefined, "only the sentence is kept");
  assert.equal(shown.best_effort[0].context_tokens, 131072, "a best-effort candidate's values come from the agent's list");
  assert.equal(shown.best_effort[0].why, undefined);
});

test("activity shortens the task's folder to a relative path, or to . when the path ends there", () => {
  const activity = new Activity("jabc123");
  const folder = "/mnt/c/Users/u/.aimeter/worktrees/jabc123";
  assert.equal(activity.shorten(folder + "/slug.js"), "slug.js");
  assert.equal(activity.shorten("cd " + folder + " && node --test"), "cd . && node --test");
  assert.equal(activity.shorten("cd " + folder + "/ && node --test"), "cd . && node --test", "a trailing separator does not leave an empty path");
  assert.equal(activity.shorten('cd "' + folder + '/"'), 'cd "."');
  assert.equal(activity.shorten(folder + "/"), ".");
  assert.equal(activity.shorten("C:\\Users\\u\\.aimeter\\snapshots\\jabc123\\src\\a.js"), "src\\a.js");
  assert.equal(activity.shorten("git checkout ai-meter/jabc123"), "git checkout ai-meter/jabc123", "only the worktrees and snapshots folders are shortened");
});

test("a tool call that never ends is reported as unfinished", () => {
  const activity = new Activity("j1");
  activity.begin("a", "bash", { command: "node --test" });
  activity.begin("b", "write", { path: "x.js", content: "x" });
  activity.end("b", { failed: false, output: "ok" });
  const shown = activity.report();

  assert.deepEqual(shown.calls, [{ tool: "bash", args: "node --test", unfinished: true }, { tool: "write", args: "x.js" }]);
  assert.deepEqual(shown.commands, [], "a command without its end has no result to report");
  assert.deepEqual(shown.files_written, ["x.js"]);
});

const managedPi = { service: "openrouter", model: "a:free", runtime: { name: "pi", where: "windows", managed: true }, pool: "openrouter:free" };

test("a managed Pi candidate is described as managed, and a read-only task says nothing about shells", async () => {
  const { workspace, calls } = setUp({ candidates: [managedPi] });
  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

  assert.equal(job.status, "completed", job.error || "");
  assert.equal(job.warnings, undefined, "a missing auth.json gives nothing to redact");
  assert.equal(calls().find((c) => c.tool === "describe_dispatch").arguments.managed, true);
  assert.equal(calls().find((c) => c.tool === "claim_capacity").arguments.managed, true, "the claim names the managed copy too: AI Meter takes a call without it for the user's own Pi");
  assert.deepEqual(job.candidate, { service: "openrouter", model: "a:free", runtime: "pi", where: "windows", managed: true, pool: "openrouter:free" });
  assert.equal(job.shell, "none");
  assert.equal(job.summary.answer, "echo via none: x", "the described arguments were used");
  assert.equal(report.full(job, jobs.root()).candidate.shell, undefined);
});

test("managed pins the managed Pi, or the user's own agents", async () => {
  const { workspace, calls } = setUp({ candidates: [managedPi, pi] });
  assert.equal((await jobs.wait(jobs.start({ task: "x", workspace, managed: false }).id, 20)).candidate.managed, undefined);
  for (const tool of ["describe_dispatch", "claim_capacity"]) assert.equal(calls().find((c) => c.tool === tool).arguments.managed, undefined, "nothing for the user's own Pi: " + tool);
  assert.equal((await jobs.wait(jobs.start({ task: "x", workspace, managed: "true" }).id, 20)).candidate.managed, true, "as the command line passes it");
});

test("the fake meter, as AI Meter, refuses the managed copy to a call that does not say managed: true", async () => {
  // Guards the fixture itself: without it, the assertions on managed: true above would prove nothing.
  setUp({ candidates: [managedPi] });
  const { withMeter } = await import("../scripts/lib/meter.mjs");
  const ask = { service: "openrouter", model: "a:free", runtime: "pi" };
  await assert.rejects(withMeter((meter) => meter.call("describe_dispatch", { ...ask, where: "windows", mode: "read_only", workspace: "C:\\w" })), /No usable Pi is set up here/);
  await assert.rejects(withMeter((meter) => meter.call("claim_capacity", { ...ask, client: "test", ttl_seconds: 60 })), /No usable Pi is set up here/);
  assert.equal((await withMeter((meter) => meter.call("claim_capacity", { ...ask, managed: true, client: "test", ttl_seconds: 60 }))).lease_id, "lease-1");
});

/** A repository with one commit, for modify tasks. */
function committedRepository(folder) {
  const repository = join(folder, "repo");
  execFileSync("git", ["init", "-q", repository]);
  writeFileSync(join(repository, "a.txt"), "a\n");
  execFileSync("git", ["-C", repository, "add", "."]);
  execFileSync("git", ["-C", repository, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-qm", "init"]);
  return repository;
}

for (const bound of [true, false]) {
  const shell = bound ? "git_bash" : "powershell";
  test(`a managed modify task runs commands through ${shell} when AI Meter ${bound ? "offers" : "withholds"} the git_bash alternative`, async () => {
    const { folder } = setUp({ candidates: [managedPi] });
    const envFile = join(folder, "env.json");
    process.env.FAKE_METER_SCENARIO = JSON.stringify({ candidates: [managedPi], env_file: envFile, git_bash: bound });
    const repository = committedRepository(folder);

    const job = await jobs.wait(jobs.start({ task: "x", workspace: repository, mode: "modify" }).id, 20);

    assert.equal(job.status, "completed", job.error || "");
    assert.equal(job.shell, shell, "AI Meter's offer decides, not whether this machine has Git");
    assert.equal(job.summary.answer, "echo via " + shell + ": x", "the arguments for that shell were used");
    assert.equal(report.full(job, jobs.root()).candidate.shell, shell, "the report says where commands ran");
    const env = JSON.parse(readFileSync(envFile, "utf8"));
    assert.equal(env.PI_CODING_AGENT_DIR, "C:\\AIMeter\\pi\\config");
    const paths = Object.keys(env).filter((key) => key.toLowerCase() === "path");
    assert.equal(paths.length, 1, "one PATH variable");
    // Git's bin folder goes first on PATH only when the plugin finds Git for Windows too, which depends on this machine:
    // Windows sets %ProgramFiles% for every new process, so a test cannot stand in its own (tests/shell.test.mjs covers
    // each case with made-up files). On Linux the plugin looks for Windows paths and finds none.
    const bin = bound && process.platform === "win32" ? findGitBash() : null;
    if (bin) assert.equal(env[paths[0]].split(";")[0], bin, "Git's bin folder comes first");
    else assert.equal(env[paths[0]], process.env[Object.keys(process.env).find((key) => key.toLowerCase() === "path")], "PATH is left as it was");
  });
}

/**
 * A job written as start() would write it, its worker then run here: start() needs the workspace to exist, and a WSL
 * share does not exist on every machine that runs the tests.
 */
async function runWritten(fields) {
  const id = "jw" + Date.now().toString(36) + Math.random().toString(16).slice(2, 6);
  mkdirSync(join(jobs.root(), id), { recursive: true });
  writeFileSync(join(jobs.root(), id, "task.txt"), "x");
  writeFileSync(join(jobs.root(), id, "job.json"), JSON.stringify({
    id, status: "queued", created_at: new Date().toISOString(), client: "test", from: "windows", mode: "read_only", timeout_seconds: 30, wanted: {},
    allow_best_effort: false, allow_free_tier: false, allow_plan: false, coordinator: null, skipped: [], ...fields,
  }));
  await jobs.runJob(id);
  return jobs.load(id);
}

const wslSkip = { service: "openrouter", model: "a:free", runtime: "pi", where: "windows", managed: true, reason: "the managed Pi works on Windows folders only; this repository is inside WSL" };

test("a managed candidate is skipped for a repository inside WSL, before AI Meter is asked to describe it", async () => {
  const { calls } = setUp({ candidates: [managedPi] });
  const job = await runWritten({ workspace: "\\\\wsl.localhost\\Ubuntu\\home\\u\\repo" });

  assert.equal(job.status, "failed");
  assert.deepEqual(job.skipped, [wslSkip]);
  assert.equal(calls().filter((c) => c.tool === "describe_dispatch" || c.tool === "claim_capacity").length, 0, "neither described nor claimed");
});

test("a repository start() found inside WSL skips the managed Pi however the workspace was spelt", async () => {
  setUp({ candidates: [managedPi] });
  const job = await runWritten({ workspace: "Z:\\repo", inside_wsl: true });
  assert.deepEqual(job.skipped, [wslSkip]);
});

test("a repository on this machine's own disk is not marked inside WSL", () => {
  const { workspace } = setUp({ candidates: [managedPi] });
  assert.equal(jobs.start({ task: "x", workspace }).inside_wsl, undefined);
});

test("the managed Pi starts without the coordinator's credentials, but with PATH and its own variables", async () => {
  const { folder, workspace } = setUp({ candidates: [managedPi] });
  const envFile = join(folder, "env.json");
  process.env.FAKE_METER_SCENARIO = JSON.stringify({ candidates: [managedPi], env_file: envFile });
  const planted = { OPENROUTER_API_KEY: "sk-or-coordinator", GITHUB_TOKEN: "ghp_coordinator", MY_SERVICE_BASE_URL: "https://example.invalid", AI_METER_HARMLESS: "kept" };
  Object.assign(process.env, planted);
  try {
    const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);
    assert.equal(job.status, "completed", job.error || "");
  } finally {
    for (const name of Object.keys(planted)) delete process.env[name];
  }
  const env = JSON.parse(readFileSync(envFile, "utf8"));
  for (const name of ["OPENROUTER_API_KEY", "GITHUB_TOKEN", "MY_SERVICE_BASE_URL"]) assert.equal(env[name], undefined, name + " must not reach the agent");
  assert.equal(env.AI_METER_HARMLESS, "kept", "everything else stays");
  assert.equal(env.PI_CODING_AGENT_DIR, "C:\\AIMeter\\pi\\config");
  assert.equal(env.PI_OFFLINE, "1");
  assert.ok(Object.keys(env).some((key) => key.toLowerCase() === "path"), "PATH stays");
});

for (const [policy, reason] of [["sandboxed", /does not know \(sandboxed\)/], [null, /without credentials for the managed Pi/]]) {
  test(`a managed Pi described with ${policy === null ? "no" : "an unknown"} environment policy is skipped, never started with the full environment`, async () => {
    const { workspace, calls } = setUp({ candidates: [managedPi], managed_policy: policy });
    const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

    assert.equal(job.status, "failed");
    assert.match(job.skipped[0].reason, reason);
    assert.equal(calls().filter((c) => c.tool === "claim_capacity").length, 0);
  });
}

/** A managed copy's configuration folder with two keys, as AI Meter writes auth.json. */
function managedConfig(folder) {
  const config = join(folder, "pi-config");
  mkdirSync(config, { recursive: true });
  const keys = { openrouter: "sk-or-v1-fixture-key-0123456789", groq: "gsk_fixture_key_abcdefgh" };
  writeFileSync(join(config, "auth.json"), JSON.stringify({ openrouter: { type: "api_key", key: keys.openrouter }, groq: { type: "api_key", key: keys.groq } }));
  return { config, keys: Object.values(keys) };
}

test("a key the managed Pi prints and quotes is removed from its answer and activity, never reaches the report, and is flagged", async () => {
  const { folder, workspace } = setUp({ candidates: [managedPi] });
  const { config, keys } = managedConfig(folder);
  process.env.FAKE_METER_SCENARIO = JSON.stringify({ candidates: [managedPi], pi_config: config, behaviour: { pi: "leak" } });

  const job = await jobs.wait(jobs.start({ task: "print the keys", workspace }).id, 20);

  assert.equal(job.status, "completed", job.error || "");
  assert.equal(job.summary.answer, "The keys are: [key removed by AI Meter] and [key removed by AI Meter].");
  assert.ok(job.warnings.includes("managed_key_redacted"));
  const saved = readFileSync(join(jobs.root(), job.id, "job.json"), "utf8");
  const shown = JSON.stringify(report.full(await jobs.wait(job.id, 0), jobs.root()));
  for (const key of keys) {
    assert.ok(!saved.includes(key), "job.json never holds a key");
    assert.ok(!shown.includes(key), "delegate_result never holds a key");
  }
  assert.match(shown, /managed_key_redacted/);
  assert.match(job.summary.activity.commands[0].output_tail, /key removed by AI Meter/, "the command's output was redacted too");
});

test("a key in the managed Pi's error output is removed from the job's error", async () => {
  const { folder, workspace } = setUp({ candidates: [managedPi] });
  const { config, keys } = managedConfig(folder);
  process.env.FAKE_METER_SCENARIO = JSON.stringify({ candidates: [managedPi], pi_config: config, behaviour: { pi: "leakerror" } });

  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

  assert.equal(job.status, "failed");
  assert.match(job.error, /error output starts: Could not parse .*\[key removed by AI Meter\]/);
  assert.ok(job.warnings.includes("managed_key_redacted"));
  const saved = readFileSync(join(jobs.root(), job.id, "job.json"), "utf8");
  for (const key of keys) assert.ok(!saved.includes(key));
});

test("a key file that exists but cannot be parsed stops the managed candidate before anything is claimed", async () => {
  const { folder, workspace, calls } = setUp({ candidates: [managedPi] });
  const { config } = managedConfig(folder);
  writeFileSync(join(config, "auth.json"), '{ "openrouter": { "key": "sk-or-v1-fixture-key-0123456789"');
  process.env.FAKE_METER_SCENARIO = JSON.stringify({ candidates: [managedPi], pi_config: config });

  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

  assert.equal(job.status, "failed");
  assert.match(job.skipped[0].reason, /key file could not be read/);
  assert.ok(!JSON.stringify(job).includes("sk-or-v1"), "the reason never quotes the file");
  assert.equal(calls().filter((c) => c.tool === "claim_capacity").length, 0);
});

test("a key added while the agent runs is removed too: the key file is read again when it ends", async () => {
  const { folder, workspace } = setUp({ candidates: [managedPi] });
  const { config } = managedConfig(folder);
  process.env.FAKE_METER_SCENARIO = JSON.stringify({ candidates: [managedPi], pi_config: config, behaviour: { pi: "rotate" } });

  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

  assert.equal(job.summary.answer, "The new key is [key removed by AI Meter].");
  assert.ok(job.warnings.includes("managed_key_redacted"));
  assert.ok(!readFileSync(join(jobs.root(), job.id, "job.json"), "utf8").includes("sk-rotated-during-the-task-42"));
});

test("when the key file cannot be read again after the agent, its answer and activity are withheld", async () => {
  const { folder, workspace } = setUp({ candidates: [managedPi] });
  const { config, keys } = managedConfig(folder);
  process.env.FAKE_METER_SCENARIO = JSON.stringify({ candidates: [managedPi], pi_config: config, behaviour: { pi: "breakkeys" } });

  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);
  const shown = report.full(job, jobs.root());

  assert.match(shown.answer, /withheld: the managed Pi's keys could not be checked/);
  assert.equal(shown.activity, undefined);
  assert.match(job.error, /withheld.*key file could not be read/);
  assert.ok(job.warnings.includes("managed_key_check_failed"));
  assert.equal(job.released, true, "the lease is still released, with the counters");
  const saved = readFileSync(join(jobs.root(), job.id, "job.json"), "utf8");
  for (const key of keys) assert.ok(!saved.includes(key));
});

/** A job's saved state and its report, as text, with no answer the agent gave and none of these keys. */
function assertWithheld(job, keys, reason) {
  const saved = readFileSync(join(jobs.root(), job.id, "job.json"), "utf8");
  const shown = JSON.stringify(report.full(job, jobs.root()));
  for (const text of [saved, shown]) {
    for (const key of keys) assert.ok(!text.includes(key), "no key");
    assert.ok(!text.includes("The key is") && !text.includes("The passing key"), "no answer the agent gave");
  }
  assert.match(job.summary.answer, /withheld/);
  assert.equal(job.summary.activity, undefined);
  assert.match(job.error, reason);
  assert.ok(job.warnings.includes("managed_key_check_failed"));
}

test("keys that changed more than once during the task withhold the output: a key may have come and gone unseen", async () => {
  const { folder, workspace } = setUp({ candidates: [managedPi] });
  const { config, keys } = managedConfig(folder);
  writeFileSync(join(config, "keys-revision"), "3");
  process.env.FAKE_METER_SCENARIO = JSON.stringify({ candidates: [managedPi], pi_config: config, behaviour: { pi: "flip" } });

  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

  assert.equal(readFileSync(join(config, "keys-revision"), "utf8"), "5");
  assertWithheld(job, [...keys, "sk-came-and-went-0123456789"], /changed more than once during the task/);
});

test("a key revision that is no longer a number after the task withholds the output", async () => {
  const { folder, workspace } = setUp({ candidates: [managedPi] });
  const { config, keys } = managedConfig(folder);
  process.env.FAKE_METER_SCENARIO = JSON.stringify({ candidates: [managedPi], pi_config: config, behaviour: { pi: "badrevision" } });

  assertWithheld(await jobs.wait(jobs.start({ task: "x", workspace }).id, 20), keys, /key revision is not a number/);
});

test("a key revision that cannot be parsed before the task stops the managed candidate", async () => {
  const { folder, workspace, calls } = setUp({ candidates: [managedPi] });
  const { config } = managedConfig(folder);
  writeFileSync(join(config, "keys-revision"), "-1");
  process.env.FAKE_METER_SCENARIO = JSON.stringify({ candidates: [managedPi], pi_config: config });

  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);
  assert.match(job.skipped[0].reason, /key revision is not a number/);
  assert.equal(calls().filter((c) => c.tool === "claim_capacity").length, 0);
});

test("after a failed key check, Git is never asked about the agent's changes, and a later failure keeps the output withheld", async () => {
  // The agent breaks the key file and its worktree's .git file, whose path names a key: describing its changes would
  // fail with an error quoting it, and before the fix that error path saved the answer unwithheld.
  const { folder } = setUp({ candidates: [managedPi] });
  const { config, keys } = managedConfig(folder);
  process.env.FAKE_METER_SCENARIO = JSON.stringify({ candidates: [managedPi], pi_config: config, behaviour: { pi: "breakgit" } });
  const repository = committedRepository(folder);

  const job = await jobs.wait(jobs.start({ task: "x", workspace: repository, mode: "modify" }).id, 20);

  assertWithheld(job, keys, /key file could not be read/);
  assert.equal(job.worktree.withheld, true);
  assert.equal(job.worktree.commits, undefined, "no Git details of the agent's work");
});

test("a Git failure after a passed key check is saved with the final keys removed", async () => {
  const { folder } = setUp({ candidates: [managedPi] });
  const { config, keys } = managedConfig(folder);
  process.env.FAKE_METER_SCENARIO = JSON.stringify({ candidates: [managedPi], pi_config: config, behaviour: { pi: "gitfails" } });
  const repository = committedRepository(folder);

  const job = await jobs.wait(jobs.start({ task: "x", workspace: repository, mode: "modify" }).id, 20);

  assert.equal(job.status, "failed");
  assert.match(job.error, /Command failed: git/);
  // Git on Linux names the broken folder, and with it the key; Git for Windows says "(NULL)" instead.
  if (/no-such-git-folder/.test(job.error)) assert.match(job.error, /no-such-git-folder-\[key removed by AI Meter\]/, "Git's error, with the key removed");
  assert.equal(job.summary.answer, "The key is [key removed by AI Meter].", "the answer attached before Git failed is saved redacted");
  assert.ok(job.warnings.includes("managed_key_redacted"));
  const saved = readFileSync(join(jobs.root(), job.id, "job.json"), "utf8");
  for (const key of keys) assert.ok(!saved.includes(key));
});

test("a modify job's Git description never carries a key: commit subjects and file names are redacted", async () => {
  const { folder } = setUp({ candidates: [managedPi] });
  const { config, keys } = managedConfig(folder);
  process.env.FAKE_METER_SCENARIO = JSON.stringify({ candidates: [managedPi], pi_config: config, behaviour: { pi: "leakcommit" } });
  const repository = committedRepository(folder);

  const job = await jobs.wait(jobs.start({ task: "x", workspace: repository, mode: "modify" }).id, 20);

  assert.equal(job.status, "completed", job.error || "");
  assert.equal(job.worktree.commits.length, 1);
  assert.match(job.worktree.commits[0], /store \[key removed by AI Meter\]$/);
  assert.deepEqual(job.worktree.files.map((f) => f.path), ["[key removed by AI Meter].txt"]);
  assert.ok(job.warnings.includes("managed_key_redacted"));
  const saved = readFileSync(join(jobs.root(), job.id, "job.json"), "utf8");
  const shown = JSON.stringify(report.full(job, jobs.root()));
  assert.ok(!saved.includes(keys[0]) && !shown.includes(keys[0]));
});

test("an agent that is not the managed copy is never redacted, whatever its answer", async () => {
  const { folder, workspace } = setUp({ candidates: [pi] });
  const { keys } = managedConfig(folder);
  const job = await jobs.wait(jobs.start({ task: keys[0], workspace }).id, 20);

  assert.equal(job.summary.answer, "echo: " + keys[0], "only the managed copy's keys are looked for");
  assert.equal(job.warnings, undefined);
});

test("an agent that exits without explaining itself in its events gets its exit code and the start of its error output in the error", async () => {
  const { workspace } = setUp({ candidates: [pi], behaviour: { pi: "nokey" } });
  const job = await jobs.wait(jobs.start({ task: "x", workspace }).id, 20);

  assert.equal(job.status, "failed");
  assert.equal(job.exit_code, 1);
  assert.equal(job.error, 'Pi exited before agent_settled. The agent exited with code 1; its error output starts: Warning: Model "x" not found for provider "openrouter". ' +
    "Using custom model id. No API key found for openrouter. Use /login to log into a provider via OAuth or API key. See: docs/providers.md");
  assert.equal(job.summary.error, job.error);
});

test("Pi's powershell tool calls are commands, as its bash ones are", () => {
  const activity = new Activity("j1");
  activity.begin("a", "powershell", { command: "Get-ChildItem *.json" });
  activity.end("a", { failed: true, output: "No such file\n\nCommand exited with code 1" });
  assert.deepEqual(activity.report().commands, [{ command: "Get-ChildItem *.json", ok: false, exit_code: 1, output_tail: "No such file\n\nCommand exited with code 1" }]);
});

test("activity keeps 50 file paths, and counts the others", () => {
  const activity = new Activity("j1");
  for (let i = 0; i < 55; i++) {
    activity.begin(i, "write", { path: "f" + i + ".js", content: "x" });
    activity.end(i, { failed: false });
  }
  activity.begin("again", "edit", { path: "f0.js" });
  activity.end("again", { failed: false });
  const shown = activity.report();

  assert.equal(shown.files_written.length, 50);
  assert.equal(shown.files_written.at(-1), "f49.js");
  assert.equal(shown.more_files_left_out, 5, "a file written twice counts once");
});
