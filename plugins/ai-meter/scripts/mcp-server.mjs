#!/usr/bin/env node
// The plugin's MCP server, used by both coordinators: Claude Code starts it from .mcp.json, Codex from mcp.json.
// It speaks newline-delimited JSON-RPC on stdio, with no dependencies. Tools return at once: a delegated task runs as
// a background job (lib/jobs.mjs), which the coordinator follows with delegate_status and delegate_result.
//
// The server may run in the plugin's folder (Codex) or the project's (Claude Code), so every tool that works on files
// takes the folder explicitly.

import { createInterface } from "node:readline";
import * as jobs from "./lib/jobs.mjs";
import { withMeter } from "./lib/meter.mjs";
import * as report from "./lib/report.mjs";

const VERSION = "0.1.0";
const MAXIMUM_WAIT_SECONDS = 50;   // Codex gives an MCP call 60 s by default.
let client = "unknown";

const text = (description) => ({ type: "string", description });
const integer = (description) => ({ type: "integer", description });
const choice = (description, ...values) => ({ type: "string", enum: values, description });
const waitSeconds = integer(`Seconds to wait for the job to end before answering, at most ${MAXIMUM_WAIT_SECONDS} (default 0).`);
const needs = {
  service: text("Only this service, such as openrouter."),
  min_context: integer("Smallest context window the task needs, in tokens."),
  cost_policy: choice("free_only (default): only models whose list states a zero price. free_or_unconfirmed: also models whose price the service does not state.", "free_only", "free_or_unconfirmed"),
};
const bestEffort = (verb) => ({
  type: "boolean",
  description: verb + " best-effort candidates: models whose service states no price or capabilities, filled in from an agent's own model list. They may fail, stop or meet unstated limits. Only with the user's agreement. Default false.",
});

const TOOLS = [
  {
    name: "delegate",
    description:
      "Hand a self-contained task to a coding agent on a free model, chosen with AI Meter: the first candidate in AI Meter's order " +
      "whose agent can be started here and whose pool has room. Returns a job id at once; follow it with delegate_result. " +
      "The workspace must be in a Git repository. read_only (default) gives the agent reading tools only, on a copy of the " +
      "files Git tracks or would track, without .env and other files whose names suggest secrets. modify runs it in a new " +
      "Git worktree on its own branch, made from the last commit; nothing is merged. The task and every file the agent " +
      "reads go to the model's provider. Give the whole context the task needs and check the answer.",
    inputSchema: {
      type: "object",
      properties: {
        task: text("The full instructions for the agent. It sees only this and the workspace."),
        workspace: text("Absolute path of the folder the task works in, inside a Git repository: usually its root."),
        mode: choice("read_only (default) or modify.", "read_only", "modify"),
        ...needs,
        model: text("Only this model id."),
        runtime: text("Only this agent, such as pi or opencode."),
        where: choice("Only agents in this place.", "windows", "wsl"),
        distro: text("Only agents in this WSL distribution."),
        allow_best_effort: bestEffort("Also use, after every ordinary candidate,"),
        timeout_seconds: integer(`Stop the agent after this long: default ${jobs.DEFAULT_TIMEOUT_SECONDS}, at most ${jobs.MAXIMUM_TIMEOUT_SECONDS}.`),
        wait_seconds: waitSeconds,
      },
      required: ["task", "workspace"],
    },
  },
  {
    name: "delegate_result",
    description: "A delegated job's report: status, the agent's answer, requests and tokens, the changed files of a modify task, and why candidates were skipped.",
    inputSchema: { type: "object", properties: { job_id: text("The job from delegate."), wait_seconds: waitSeconds }, required: ["job_id"] },
  },
  {
    name: "delegate_status",
    description: "The latest delegated jobs, newest first, with status and candidate.",
    inputSchema: { type: "object", properties: { limit: integer("How many jobs, default 10.") } },
  },
  {
    name: "delegate_cancel",
    description: "Stop a delegated job's agent and release its capacity lease.",
    inputSchema: { type: "object", properties: { job_id: text("The job to stop.") }, required: ["job_id"] },
  },
  {
    name: "delegate_candidates",
    description: "Where a task could run now for free, from AI Meter: candidates in order, each pool's room, why other models are left out and which agents were found. Starts nothing.",
    inputSchema: { type: "object", properties: { ...needs, include_best_effort: bestEffort("Also list") } },
  },
];

async function call(name, args) {
  const wait = Math.min(Math.max(Number(args.wait_seconds) || 0, 0), MAXIMUM_WAIT_SECONDS);
  switch (name) {
    case "delegate": {
      const job = jobs.start({ ...args, client });
      return report.full(await jobs.wait(job.id, wait), jobs.root());
    }
    case "delegate_result":
      return report.full(await jobs.wait(args.job_id, wait), jobs.root());
    case "delegate_status":
      return { jobs: jobs.list(Number(args.limit) || 10).map(report.brief) };
    case "delegate_cancel":
      return report.full(await jobs.cancel(args.job_id), jobs.root());
    case "delegate_candidates":
      return report.candidates(await withMeter((meter) => meter.call("list_candidates", { tools: true, ...args })));
    default:
      throw new Error("Unknown tool: " + name);
  }
}

async function handle(message) {
  const { id, method, params } = message;
  switch (method) {
    case "initialize":
      client = params?.clientInfo?.name || client;
      return {
        protocolVersion: params?.protocolVersion || "2025-06-18",
        capabilities: { tools: {} },
        serverInfo: { name: "ai-meter-delegate", version: VERSION },
      };
    case "ping":
      return {};
    case "tools/list":
      return { tools: TOOLS };
    case "tools/call":
      try {
        const result = await call(params.name, params.arguments || {});
        return { content: [{ type: "text", text: JSON.stringify(result, null, 2) }], structuredContent: result };
      } catch (error) {
        return { content: [{ type: "text", text: error.message }], isError: true };
      }
    default:
      if (id === undefined) return undefined;   // A notification needs no answer.
      throw Object.assign(new Error("Method not found: " + method), { code: -32601 });
  }
}

const send = (message) => process.stdout.write(JSON.stringify({ jsonrpc: "2.0", ...message }) + "\n");

createInterface({ input: process.stdin }).on("line", async (line) => {
  if (!line.trim()) return;
  let message;
  try {
    message = JSON.parse(line);
  } catch {
    return send({ id: null, error: { code: -32700, message: "Parse error" } });
  }
  try {
    const result = await handle(message);
    if (message.id !== undefined && result !== undefined) send({ id: message.id, result });
  } catch (error) {
    if (message.id !== undefined) send({ id: message.id, error: { code: error.code || -32603, message: error.message } });
  }
});
