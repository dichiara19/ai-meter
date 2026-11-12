// A stand-in for AI Meter's relay: answers the delegation tools over stdio the way AI Meter does, and logs each call
// to FAKE_METER_LOG (JSONL). FAKE_METER_SCENARIO (JSON) sets the candidates, the agents it cannot describe
// ("runtime@where"), the describe_dispatch errors by "runtime@where", the pools that refuse leases, the warnings,
// the runtimes found and unsupported, the best-effort candidates (returned only with include_best_effort, counted
// otherwise), the free-tier candidates (returned only with include_free_tier) and the budget a lease grants, the
// tools it never answers (`silent`, as a relay that stays up but hangs), and how each fake agent behaves.

import { appendFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { createInterface } from "node:readline";
import { fileURLToPath } from "node:url";

const scenario = JSON.parse(process.env.FAKE_METER_SCENARIO || "{}");
const agent = join(dirname(fileURLToPath(import.meta.url)), "fake-agent.mjs");
let leases = 0;
let readings = scenario.still_reading || 0;   // describe_dispatch answers "still reading" this many times first.

const tools = {
  list_candidates: (args) => ({
    candidates: [...(scenario.candidates || []), ...(args.include_free_tier ? scenario.free_tier || [] : [])],
    ...(scenario.best_effort && args.include_best_effort ? { best_effort: scenario.best_effort } : {}),
    ...(scenario.best_effort && !args.include_best_effort ? { best_effort_available: scenario.best_effort.length } : {}),
    pools: [],
    excluded: scenario.excluded || [],
    runtimes: { found: [], unsupported: scenario.unsupported_runtimes || [], key_discovery: true },
  }),
  describe_dispatch: (args) => {
    if (readings-- > 0) return { error: "AI Meter is still reading the first data; try again in a few seconds." };
    const error = (scenario.errors || {})[args.runtime + "@" + args.where];
    if (error) return { error };
    if ((scenario.unsupported || []).includes(args.runtime + "@" + args.where)) return { supported: false, reason: "not verified here", runtime: args.runtime, where: args.where };
    return {
      supported: true,
      runtime: args.runtime,
      where: args.where,
      version: "1.0.0",
      mode: args.mode,
      program: process.execPath,
      arguments: [agent, args.runtime, (scenario.behaviour || {})[args.runtime] || "ok"],
      environment: { FAKE_AGENT_MODE: args.mode },
      working_directory: args.workspace,
      task_input: "stdin",
      output: { format: "jsonl", cost: "per request" },
      notes: [],
      ...(args.distro ? { distro: args.distro } : {}),
      ...(scenario.warnings ? { warnings: scenario.warnings } : {}),
      ...(PLAN_AGENTS.includes(args.runtime) ? planDispatch(args) : {}),
    };
  },
  claim_capacity: (args) => {
    const onPlan = PLAN_AGENTS.includes(args.runtime);
    const pool = args.service + (onPlan ? ":plan" : ":free");
    if ((scenario.refuse || []).includes(pool)) return { refused: true, pool, headroom: { blocking: { reason: onPlan ? "plan_reserve" : "leased" } } };
    return {
      lease_id: "lease-" + ++leases, pool, expires_at: "2026-11-02T12:00:00+01:00", ...(scenario.budget ? { budget: scenario.budget } : {}),
      ...(onPlan ? { billing: "included_in_plan", plan: { service: args.service, binding_window: "5h", windows: PLAN_WINDOWS, expiring: false } } : {}),
    };
  },
  release_capacity: (args) => ({ lease_id: args.lease_id, released: true }),
  get_limits: (args) => ({ services: [{ service: args.service, read_at: "2026-11-02T14:00:00+01:00", limits: [{ name: "Session 5 h", kind: "5h", remaining_percent: 55, resets_at: "2026-11-02T15:00:00+01:00" }] }] }),
};

// Plan agents (ADR 0003): AI Meter describes them with a clean environment, a sign-in check and the plan's billing.
const PLAN_AGENTS = ["claude", "codex"];
const PLAN_WINDOWS = [{ window: "5h", remaining_percent: 60, reserve_percent: 40, resets_at: "2026-11-02T15:00:00+01:00" }];

function planDispatch(args) {
  const signIn = (scenario.sign_in || {})[args.runtime] || "plan";
  return {
    environment_policy: "clean",
    billing: "included_in_plan",
    environment: { FAKE_AGENT_MODE: args.mode, ...(scenario.env_file ? { FAKE_ENV_FILE: scenario.env_file } : {}), ...(scenario.plan_environment || {}) },
    sign_in_check: args.runtime === "claude"
      ? { program: process.execPath, arguments: [agent, "signin", "claude", signIn], json_field: "authMethod", expect: "claude.ai" }
      : { program: process.execPath, arguments: [agent, "signin", "codex", signIn], text: "Logged in using ChatGPT" },
  };
}

const send = (message) => process.stdout.write(JSON.stringify({ jsonrpc: "2.0", ...message }) + "\n");

createInterface({ input: process.stdin }).on("line", (line) => {
  if (!line.trim()) return;
  const { id, method, params } = JSON.parse(line);
  if (id === undefined) return;
  if (method === "initialize") return send({ id, result: { protocolVersion: params.protocolVersion, capabilities: { tools: {} }, serverInfo: { name: "fake-meter" } } });
  if (process.env.FAKE_METER_LOG) appendFileSync(process.env.FAKE_METER_LOG, JSON.stringify({ tool: params.name, arguments: params.arguments }) + "\n");
  if ((scenario.silent || []).includes(params.name)) return;
  const result = tools[params.name](params.arguments);
  send({ id, result: { content: [{ type: "text", text: JSON.stringify(result) }], structuredContent: result, isError: Boolean(result.error) } });
});
