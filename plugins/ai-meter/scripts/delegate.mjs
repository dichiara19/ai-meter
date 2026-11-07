#!/usr/bin/env node
// Command line for the delegation jobs, for a terminal and for the background worker the MCP server starts.
//
//   node delegate.mjs start --workspace <folder> [--mode modify] [--service s] [--model m] [--runtime r] [--where w]
//                           [--allow-best-effort true] [--allow-free-tier true] [--timeout <seconds>] [--wait <seconds>]
//                                                                        the task is read from stdin
//   node delegate.mjs result <job> [--wait <seconds>]
//   node delegate.mjs status
//   node delegate.mjs cancel <job>
//   node delegate.mjs candidates [--service s] [--include-best-effort true] [--include-free-tier true]
//   node delegate.mjs run-job <job>                                       the worker; not for direct use
//
// Every command prints JSON.

import * as jobs from "./lib/jobs.mjs";
import { withMeter } from "./lib/meter.mjs";
import * as report from "./lib/report.mjs";

function options(args) {
  const named = {};
  const positional = [];
  for (let i = 0; i < args.length; i++) {
    if (args[i].startsWith("--")) named[args[i].slice(2).replace(/-/g, "_")] = args[++i];
    else positional.push(args[i]);
  }
  if (named.min_context) named.min_context = Number(named.min_context);
  for (const flag of ["include_best_effort", "allow_best_effort", "include_free_tier", "allow_free_tier"]) if (flag in named) named[flag] = named[flag] === "true";
  return { named, positional };
}

async function readStdin() {
  let data = "";
  process.stdin.setEncoding("utf8");
  for await (const chunk of process.stdin) data += chunk;
  return data;
}

async function main() {
  const [command, ...rest] = process.argv.slice(2);
  const { named, positional } = options(rest);
  const wait = Number(named.wait) || 0;
  switch (command) {
    case "run-job":
      return void (await jobs.runJob(positional[0]));
    case "start": {
      const job = jobs.start({ ...named, timeout_seconds: named.timeout, task: await readStdin(), client: named.client || "terminal" });
      return report.full(await jobs.wait(job.id, wait), jobs.root());
    }
    case "result":
      return report.full(await jobs.wait(positional[0], wait), jobs.root());
    case "status":
      return { jobs: jobs.list().map(report.brief) };
    case "cancel":
      return report.full(await jobs.cancel(positional[0]), jobs.root());
    case "candidates":
      return report.candidates(await withMeter((meter) => meter.call("list_candidates", { tools: true, ...named })));
    default:
      throw new Error("Commands: start, result, status, cancel, candidates.");
  }
}

main().then(
  (result) => result !== undefined && console.log(JSON.stringify(result, null, 2)),
  (error) => {
    console.error(error.message);
    process.exitCode = 1;
  },
);
