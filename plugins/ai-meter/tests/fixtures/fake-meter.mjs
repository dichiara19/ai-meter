// A stand-in for AI Meter's relay: answers the delegation tools over stdio the way AI Meter does, and logs each call
// to FAKE_METER_LOG (JSONL). FAKE_METER_SCENARIO (JSON) sets the candidates, the agents it cannot describe
// ("runtime@where"), the describe_dispatch errors by "runtime@where", the pools that refuse leases, the warnings,
// the runtimes found and unsupported, and how each fake agent behaves.

import { appendFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { createInterface } from "node:readline";
import { fileURLToPath } from "node:url";

const scenario = JSON.parse(process.env.FAKE_METER_SCENARIO || "{}");
const agent = join(dirname(fileURLToPath(import.meta.url)), "fake-agent.mjs");
let leases = 0;
let readings = scenario.still_reading || 0;   // describe_dispatch answers "still reading" this many times first.

const tools = {
  list_candidates: () => ({
    candidates: scenario.candidates || [],
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
    };
  },
  claim_capacity: (args) => {
    const pool = args.service + ":free";
    if ((scenario.refuse || []).includes(pool)) return { refused: true, pool, headroom: { blocking: { reason: "leased" } } };
    return { lease_id: "lease-" + ++leases, pool, expires_at: "2026-11-02T12:00:00+01:00" };
  },
  release_capacity: (args) => ({ lease_id: args.lease_id, released: true }),
};

const send = (message) => process.stdout.write(JSON.stringify({ jsonrpc: "2.0", ...message }) + "\n");

createInterface({ input: process.stdin }).on("line", (line) => {
  if (!line.trim()) return;
  const { id, method, params } = JSON.parse(line);
  if (id === undefined) return;
  if (method === "initialize") return send({ id, result: { protocolVersion: params.protocolVersion, capabilities: { tools: {} }, serverInfo: { name: "fake-meter" } } });
  if (process.env.FAKE_METER_LOG) appendFileSync(process.env.FAKE_METER_LOG, JSON.stringify({ tool: params.name, arguments: params.arguments }) + "\n");
  const result = tools[params.name](params.arguments);
  send({ id, result: { content: [{ type: "text", text: JSON.stringify(result) }], structuredContent: result, isError: Boolean(result.error) } });
});
