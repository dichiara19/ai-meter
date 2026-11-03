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
    assert.deepEqual(released(calls), { lease_id: "lease-1", outcome: "failed", requests: 1, rate_limited: 0 });
    assert.throws(() => process.kill(job.agent_pid, 0), "the agent is gone");
  });
}

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
  assert.deepEqual(job.worktree.files, [{ status: "M", path: "a.txt" }, { status: "??", path: "made-by-agent.txt" }]);
  assert.match(job.worktree.diff_stat, /a\.txt/);
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
  assert.equal(existsSync(job.snapshot.path), false, "the snapshot is removed when the task ends");
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
  assert.deepEqual(job.worktree.files, [{ status: "M", path: "a.txt" }, { status: "??", path: "made-by-agent.txt" }]);
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
