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
