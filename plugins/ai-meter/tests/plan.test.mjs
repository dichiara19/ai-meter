// Delegation on subscription plans (ADR 0003): the clean environment, the sign-in check, the coordinator's service,
// the readers of Claude Code's and Codex's events (replaying runs recorded live on 2026-11-12) and whole jobs with a
// fake AI Meter. Run with node --test plugins/ai-meter/tests

import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { readerFor, summarise } from "../scripts/lib/agents.mjs";
import * as jobs from "../scripts/lib/jobs.mjs";
import { checkSignIn, cleanEnvironment, coordinatorOf, judge } from "../scripts/lib/plan.mjs";
import * as report from "../scripts/lib/report.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const agent = join(here, "fixtures", "fake-agent.mjs");
const codex = { service: "openai", model: "gpt-a", runtime: { name: "codex", where: "wsl" }, pool: "openai:plan", billing: "included_in_plan" };
const claude = { service: "claude", model: "sonnet", runtime: { name: "claude", where: "wsl" }, pool: "claude:plan", billing: "included_in_plan" };

function setUp(scenario) {
  const folder = mkdtempSync(join(tmpdir(), "ai-meter-plan-"));
  process.env.AI_METER_JOBS = join(folder, "jobs");
  process.env.AI_METER_RELAY = JSON.stringify([process.execPath, join(here, "fixtures", "fake-meter.mjs")]);
  process.env.FAKE_METER_SCENARIO = JSON.stringify({ env_file: join(folder, "agent-env.json"), ...scenario });
  process.env.FAKE_METER_LOG = join(folder, "meter.jsonl");
  process.env.AI_METER_PLACE = process.platform === "win32" ? "windows" : "wsl";
  const workspace = join(folder, "workspace");
  execFileSync("git", ["init", "-q", workspace]);
  return {
    workspace,
    agentEnvironment: () => JSON.parse(readFileSync(join(folder, "agent-env.json"), "utf8")),
    agentStarted: () => existsSync(join(folder, "agent-env.json")),
    calls: () => (existsSync(process.env.FAKE_METER_LOG) ? readFileSync(process.env.FAKE_METER_LOG, "utf8").trim().split("\n").map((l) => JSON.parse(l)) : []),
  };
}

/** Feed a recorded run to a reader, as the worker does, and sum it up. */
function replay(runtime, exitCode = 0, transform = (events) => events) {
  const events = readFileSync(join(here, "fixtures", runtime + "-plan.jsonl"), "utf8").trim().split("\n").map((l) => JSON.parse(l));
  const reader = readerFor(runtime, "job1");
  for (const event of transform(events)) reader.feed(event);
  return { reader, summary: summarise(reader, exitCode, reader.paid ? "paid" : null) };
}

test("a plan agent's environment keeps only what a program needs, never API keys or the coordinator's session", () => {
  const base = {
    HOME: "/home/me", USER: "me", PATH: "/home/me/bin:/usr/bin", LANG: "C.UTF-8",
    ANTHROPIC_API_KEY: "sk-ant-x", ANTHROPIC_BASE_URL: "https://proxy.example", ANTHROPIC_AUTH_TOKEN: "t", CLAUDE_CODE_OAUTH_TOKEN: "o",
    CLAUDE_CODE_USE_BEDROCK: "1", CLAUDECODE: "1", CLAUDE_CODE_ENTRYPOINT: "cli", CLAUDE_CODE_SSE_PORT: "1234",
    OPENAI_API_KEY: "sk-x", OPENAI_BASE_URL: "https://proxy.example", CODEX_API_KEY: "sk-y", CODEX_HOME: "/elsewhere", AWS_BEARER_TOKEN_BEDROCK: "b",
    WSLENV: "ANTHROPIC_API_KEY/u", NODE_OPTIONS: "--require x", SOMETHING_ELSE: "y",
  };
  const linux = cleanEnvironment({ PATH: "/home/me/.local/bin:/usr/bin", FAKE: "1", ANTHROPIC_API_KEY: "from-the-description" }, base, "linux");
  assert.deepEqual(Object.keys(linux).sort(), ["FAKE", "HOME", "LANG", "PATH", "USER"]);
  assert.equal(linux.PATH, "/home/me/.local/bin:/usr/bin", "the agent's PATH from its description wins");

  const windows = cleanEnvironment({}, { SystemRoot: "C:\\Windows", USERPROFILE: "C:\\Users\\me", Path: "C:\\tools", OPENAI_API_KEY: "sk-x", APPDATA: "a" }, "win32");
  assert.deepEqual(windows, { SystemRoot: "C:\\Windows", USERPROFILE: "C:\\Users\\me", APPDATA: "a", PATH: "C:\\Windows\\System32" });
});

test("the sign-in check accepts only a plan sign-in, and reads nothing else of the output", async () => {
  const claudeCheck = { program: process.execPath, arguments: [agent, "signin", "claude", "plan"], json_field: "authMethod", expect: "claude.ai" };
  assert.deepEqual(await checkSignIn(claudeCheck, process.env), { ok: true });
  const onKey = await checkSignIn({ ...claudeCheck, arguments: [agent, "signin", "claude", "apikey"] }, process.env);
  assert.equal(onKey.ok, false);
  assert.match(onKey.reason, /api_key, not claude\.ai/);
  assert.doesNotMatch(onKey.reason, /example\.invalid/, "the account's email must not reach the report");

  const codexCheck = { program: process.execPath, arguments: [agent, "signin", "codex", "plan"], text: "Logged in using ChatGPT" };
  assert.deepEqual(await checkSignIn(codexCheck, process.env), { ok: true }, "Codex writes its status to stderr");
  const codexOnKey = await checkSignIn({ ...codexCheck, arguments: [agent, "signin", "codex", "apikey"] }, process.env);
  assert.equal(codexOnKey.ok, false);
  assert.doesNotMatch(codexOnKey.reason, /sk-proj/);

  assert.equal((await checkSignIn(null, process.env)).ok, false, "no check means no plan agent");
  assert.equal((await checkSignIn({ program: join(here, "missing-program"), arguments: [] }, process.env)).ok, false);
  assert.equal(judge({ json_field: "authMethod", expect: "claude.ai" }, "not json", "").ok, false);
});

test("the coordinator's service comes from its client name", () => {
  assert.equal(coordinatorOf("claude-code"), "claude");
  assert.equal(coordinatorOf("codex-mcp-client"), "openai");
  assert.equal(coordinatorOf("unknown"), null);
  assert.equal(coordinatorOf(undefined), null);
});

test("Claude Code's recorded run gives the answer, requests, tokens, its read and the plan's windows after it", () => {
  const { summary } = replay("claude");
  assert.equal(summary.outcome, "completed");
  assert.equal(summary.answer, "Plan delegation probe");
  assert.equal(summary.requests, 2, "two assistant messages with distinct ids");
  assert.equal(summary.input_tokens, 4 + 1929 + 8206);
  assert.equal(summary.output_tokens, 164);
  assert.equal(summary.paid_request, false);
  assert.equal(summary.cost, 0, "the result's total_cost_usd is what the API would charge, not a charge to the plan");
  assert.deepEqual(summary.activity.calls.map((c) => c.tool), ["read"]);
  assert.deepEqual(summary.plan_after.map((w) => [w.window, w.remaining_percent]), [["5h", 33], ["7d", 77]]);
});

test("Claude Code on an API key, or using extra usage, is billed: the reader stops it", () => {
  const onKey = replay("claude", 0, (events) => [{ ...events[0], apiKeySource: "ANTHROPIC_API_KEY" }]);
  assert.equal(onKey.reader.paid, true);
  assert.equal(onKey.summary.outcome, "failed");
  assert.match(onKey.summary.error, /API key \(ANTHROPIC_API_KEY\)/);

  const overage = replay("claude", 0, (events) => [events[0], { type: "rate_limit_event", rate_limit_info: { status: "allowed", isUsingOverage: true } }]);
  assert.equal(overage.reader.paid, true);
  assert.match(overage.summary.error, /extra usage/);
});

test("a Claude Code result without success fails with its message, and a reached limit counts as a rate limit", () => {
  const { summary } = replay("claude", 0, (events) => [events[0], { type: "rate_limit_event", rate_limit_info: { status: "rejected" } },
    { type: "result", subtype: "success", is_error: true, result: "Claude AI usage limit reached" }]);
  assert.equal(summary.outcome, "failed");
  assert.equal(summary.error, "Claude AI usage limit reached");
  assert.equal(summary.rate_limited, 2, "the rejected window and the error both count");
  assert.match(replay("claude", 0, (events) => events.slice(0, 3)).summary.error, /without its result event/);
});

test("Codex's recorded run gives the answer, tokens and its command; its requests are unknown", () => {
  const { summary } = replay("codex");
  assert.equal(summary.outcome, "completed");
  assert.equal(summary.answer, "Plan delegation probe");
  assert.equal(summary.requests, null, "Codex does not report its requests");
  assert.equal(summary.input_tokens, 36802);
  assert.equal(summary.output_tokens, 46);
  assert.deepEqual(summary.activity.commands.map((c) => [c.ok, c.exit_code]), [[true, 0]]);
  assert.match(summary.activity.commands[0].command, /head -n 1 hello\.txt/);

  const failed = replay("codex", 1, (events) => [events[0], { type: "turn.failed", error: { message: "You've hit your usage limit." } }]);
  assert.equal(failed.summary.outcome, "failed");
  assert.equal(failed.summary.rate_limited, 1);
  assert.match(replay("codex", 0, (events) => events.slice(0, 4)).summary.error, /turn\.completed/);
});

test("a plan task from Claude Code runs Codex on the ChatGPT plan with a clean environment and reports the window it drew on", async () => {
  const { workspace, calls, agentEnvironment } = setUp({ candidates: [codex] });
  process.env.OPENAI_API_KEY = "sk-must-not-reach-the-agent";
  process.env.ANTHROPIC_API_KEY = "sk-ant-must-not-reach-the-agent";
  let job;
  try {
    job = await jobs.wait(jobs.start({ task: "first line?", workspace, client: "claude-code", allow_plan: true }).id, 20);
  } finally {
    delete process.env.OPENAI_API_KEY;
    delete process.env.ANTHROPIC_API_KEY;
  }

  assert.equal(job.status, "completed", job.error);
  assert.equal(job.summary.answer, "Plan delegation probe");
  const listed = calls().find((c) => c.tool === "list_candidates").arguments;
  assert.equal(listed.include_plan, true);
  assert.equal(listed.coordinator, "claude", "Claude Code's own plan must be named so AI Meter leaves it out");
  assert.equal(calls().find((c) => c.tool === "describe_dispatch").arguments.coordinator, "claude");
  assert.equal(calls().find((c) => c.tool === "claim_capacity").arguments.coordinator, "claude");

  const env = agentEnvironment();
  for (const name of ["OPENAI_API_KEY", "ANTHROPIC_API_KEY", "AI_METER_JOBS", "FAKE_METER_SCENARIO", "AI_METER_RELAY"]) assert.equal(env[name], undefined, name + " reached the agent");
  assert.equal(env.FAKE_AGENT_MODE, "read_only", "the description's own variables are kept");

  const full = report.full(job, jobs.root());
  assert.equal(full.candidate.billing, "included_in_plan");
  assert.equal(full.plan.service, "openai");
  assert.equal(full.plan.window, "5h");
  assert.deepEqual(full.plan.before, [{ window: "5h", remaining_percent: 60, reserve_percent: 40, resets_at: "2026-11-02T15:00:00+01:00" }]);
  assert.deepEqual(full.plan.after, [{ window: "5h", remaining_percent: 55, resets_at: "2026-11-02T15:00:00+01:00", source: "ai_meter" }],
    "Codex does not report the windows: AI Meter reads them again");
  assert.match(full.plan.note, /not billed/);
  assert.equal(calls().find((c) => c.tool === "release_capacity").arguments.requests, null);
});

test("Claude Code's own windows are the room after the task, without asking AI Meter again", async () => {
  const { workspace, calls } = setUp({ candidates: [claude] });
  const job = await jobs.wait(jobs.start({ task: "x", workspace, client: "codex-mcp-client", allow_plan: true }).id, 20);
  assert.equal(job.status, "completed", job.error);
  assert.equal(calls().find((c) => c.tool === "list_candidates").arguments.coordinator, "openai");
  assert.deepEqual(job.plan_after.map((w) => w.source), ["agent", "agent"]);
  assert.equal(calls().some((c) => c.tool === "get_limits"), false);
});

test("a plan agent signed in with an API key is skipped before its lease, and never started", async () => {
  const { workspace, calls, agentStarted } = setUp({ candidates: [codex], sign_in: { codex: "apikey" } });
  const job = await jobs.wait(jobs.start({ task: "x", workspace, client: "claude-code", allow_plan: true }).id, 20);
  assert.equal(job.status, "failed");
  assert.match(job.skipped[0].reason, /API key/);
  assert.equal(calls().some((c) => c.tool === "claim_capacity"), false);
  assert.equal(agentStarted(), false);
});

test("Claude Code that turns to extra usage is stopped, and the task fails with why", async () => {
  const { workspace, calls } = setUp({ candidates: [claude], behaviour: { claude: "overage" } });
  const job = await jobs.wait(jobs.start({ task: "x", workspace, client: "codex", allow_plan: true }).id, 30);
  assert.equal(job.status, "failed");
  assert.match(job.error, /Stopped: Claude's extra usage/);
  assert.equal(job.summary.paid_request, true);
  assert.equal(calls().find((c) => c.tool === "release_capacity").arguments.outcome, "failed");
});

test("without allow_plan the plugin does not ask for plans", async () => {
  const { workspace, calls } = setUp({ candidates: [] });
  await jobs.wait(jobs.start({ task: "x", workspace, client: "claude-code" }).id, 20);
  const listed = calls().find((c) => c.tool === "list_candidates").arguments;
  assert.equal(listed.include_plan, undefined);
});

test("delegate_candidates keeps a plan candidate's billing and windows", () => {
  const found = report.candidates({
    candidates: [{ ...codex, plan: { windows: [{ window: "5h" }], binding_window: "5h", expiring: true, read_at: "x" }, rank: { why: "Included in the openai plan" } }],
    pools: [], excluded: [{ service: "claude", reason: "same_plan_as_coordinator", models: ["sonnet"], count: 1 }, { service: "claude", reason: "plan_reserve", models: ["opus"], count: 1 }],
    runtimes: {},
  });
  assert.equal(found.candidates[0].billing, "included_in_plan");
  assert.deepEqual(found.candidates[0].plan, { windows: [{ window: "5h" }], binding_window: "5h", expiring: true });
  assert.equal(found.excluded[0].models, undefined);
  assert.deepEqual(found.excluded[1].models, ["opus"], "a plan below its reserve comes back at a reset: its models are named");
});
