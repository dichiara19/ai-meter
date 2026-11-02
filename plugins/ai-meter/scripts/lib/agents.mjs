// Reading a coding agent's JSONL events, as describe_dispatch's `output` explains them for each runtime
// (docs/plans/spike-agents.md). A reader takes one parsed event at a time and, at the end, sums up the task for
// release_capacity and for the coordinator: outcome, requests, tokens, rate limits, cost and the agent's final answer.
//
// Cost: each agent prices its requests from its own model list. A request priced above zero was paid, so `paid`
// tells the plugin to stop the agent; a zero price does not prove the request was free (an OpenRouter BYOK fallback
// is billed by the provider, out of the agent's sight). OpenRouter does name a BYOK provider in its error bodies
// ("is_byok":true), so an error that shows one is reported as byok_fallback.

/** Shared counters; the readers below add to them. */
class Reader {
  constructor() {
    this.requests = 0;
    this.inputTokens = 0;
    this.outputTokens = 0;
    this.rateLimited = 0;
    this.cost = 0;
    this.paid = false;
    this.error = null;
    this.byok = false;
    this.answer = "";
  }

  fail(message) {
    this.error = message;
    if (/\b429\b/.test(message)) this.rateLimited++;
    if (/"is_byok"\s*:\s*true/.test(message)) this.byok = true;
  }

  price(cost) {
    if (typeof cost !== "number" || !(cost > 0)) return;
    this.cost += cost;
    this.paid = true;
  }
}

/** Pi 1.x in JSON mode. The exit code stays 0 on errors, so the outcome is the last assistant stopReason. */
class PiReader extends Reader {
  constructor() {
    super();
    this.settled = false;
    this.stopReason = null;
  }

  feed(event) {
    if (event.type === "turn_end") this.requests++;
    if (event.type === "agent_settled") this.settled = true;
    if (event.type !== "message_end" || event.message?.role !== "assistant") return;

    const message = event.message;
    const usage = message.usage || {};
    this.inputTokens += (usage.input || 0) + (usage.cacheRead || 0) + (usage.cacheWrite || 0);
    this.outputTokens += usage.output || 0;
    this.price(usage.cost?.total);
    this.stopReason = message.stopReason || this.stopReason;
    if (message.errorMessage) this.fail(message.errorMessage);
    const text = textOf(message.content);
    if (text) this.answer = text;
  }

  outcome() {
    if (!this.settled) return "failed";   // It exited before its last event.
    if (this.stopReason === "error") return "failed";
    if (this.stopReason === "aborted") return "cancelled";
    return "completed";
  }
}

/** OpenCode 1.x with --format json. The exit code is reliable: 1 with an error event on failure. */
class OpenCodeReader extends Reader {
  constructor() {
    super();
    this.step = [];
    this.exitCode = null;
  }

  feed(event) {
    if (event.type === "step_start") this.step = [];
    if (event.type === "text" && event.part?.text) this.step.push(event.part.text);
    if (event.type === "step_finish") {
      this.requests++;
      const tokens = event.part?.tokens || {};
      this.inputTokens += (tokens.input || 0) + (tokens.cache?.read || 0) + (tokens.cache?.write || 0);
      this.outputTokens += (tokens.output || 0) + (tokens.reasoning || 0);
      this.price(event.part?.cost);
      if (this.step.length) this.answer = this.step.join("\n");
    }
    if (event.type === "error") this.fail(event.error?.data?.message || event.error?.name || "error");
  }

  outcome() {
    return this.exitCode === 0 && !this.error ? "completed" : "failed";
  }
}

const READERS = { pi: PiReader, opencode: OpenCodeReader };

/**
 * Whether the plugin can read a runtime's events. AI Meter's agent table is data and can grow (or be overridden in
 * %APPDATA%\AIMeter\agents), but each event format needs code here, so other runtimes are skipped before a claim.
 */
export const canRead = (runtime) => Object.hasOwn(READERS, runtime);

export function readerFor(runtime) {
  if (!canRead(runtime)) throw new Error("No event reader for " + runtime + ".");
  return new READERS[runtime]();
}

/**
 * The summary of a task. stopped is "cancelled", "timed_out" or "paid" when the plugin ended it; tokens are then
 * unknown. A task stopped for a paid request failed.
 */
export function summarise(reader, exitCode, stopped) {
  reader.exitCode = exitCode;
  return {
    outcome: stopped === "paid" ? "failed" : stopped || reader.outcome(),
    requests: reader.requests,
    rate_limited: reader.rateLimited,
    input_tokens: stopped ? null : reader.inputTokens,
    output_tokens: stopped ? null : reader.outputTokens,
    cost: reader.cost,
    paid_request: reader.paid,
    byok_fallback: reader.byok,
    error: stopped === "paid" ? "Stopped: a request was priced as paid (" + reader.cost + "), so the free route was not used." : reader.error,
    answer: reader.answer,
  };
}

function textOf(content) {
  if (typeof content === "string") return content;
  if (!Array.isArray(content)) return "";
  return content.filter((part) => part.type === "text" && part.text).map((part) => part.text).join("\n");
}
