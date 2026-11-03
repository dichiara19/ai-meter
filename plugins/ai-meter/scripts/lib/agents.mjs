// Reading a coding agent's JSONL events, as describe_dispatch's `output` explains them for each runtime
// (docs/plans/spike-agents.md). A reader takes one parsed event at a time and, at the end, sums up the task for
// release_capacity and for the coordinator: outcome, requests, tokens, rate limits, cost and the agent's final answer.
//
// A task is completed only on positive evidence: the agent's own final event with a normal end. An answer cut off
// for length, an unknown end, or output with none of the expected events is a failure with the reason, never a
// success with an empty answer. Tokens are null, not zero, when no event reported any.
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
    this.usageSeen = false;
  }

  /** Why the run did not end normally, or null when it did. */
  incomplete() {
    return null;
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
    if (message.usage) this.usageSeen = true;
    this.inputTokens += (usage.input || 0) + (usage.cacheRead || 0) + (usage.cacheWrite || 0);
    this.outputTokens += usage.output || 0;
    this.price(usage.cost?.total);
    this.stopReason = message.stopReason || this.stopReason;
    if (message.errorMessage) this.fail(message.errorMessage);
    const text = textOf(message.content);
    if (text) this.answer = text;
  }

  outcome() {
    if (this.stopReason === "aborted") return "cancelled";
    return this.stopReason === "error" || this.incomplete() ? "failed" : "completed";
  }

  incomplete() {
    if (!this.settled) return "Pi exited before agent_settled.";
    if (this.stopReason === "error") return "Pi's last request failed.";   // Its errorMessage, when there is one, says why.
    if (this.stopReason === "length") return "Pi's answer was cut off: the model reached its output limit (stopReason length).";
    if (this.stopReason !== "stop") return "Pi ended without a final answer (stopReason " + (this.stopReason || "missing") + ").";
    return null;
  }
}

/**
 * OpenCode 1.x with --format json. The exit code is reliable: 1 with an error event on failure. A run that succeeded
 * ends with a step_finish whose reason is "stop"; "tool-calls" means the model wanted to go on.
 */
class OpenCodeReader extends Reader {
  constructor() {
    super();
    this.step = [];
    this.exitCode = null;
    this.lastReason = null;
  }

  feed(event) {
    if (event.type === "step_start") this.step = [];
    if (event.type === "text" && event.part?.text) this.step.push(event.part.text);
    if (event.type === "step_finish") {
      this.requests++;
      this.lastReason = event.part?.reason || "missing";
      if (event.part?.tokens) this.usageSeen = true;
      const tokens = event.part?.tokens || {};
      this.inputTokens += (tokens.input || 0) + (tokens.cache?.read || 0) + (tokens.cache?.write || 0);
      this.outputTokens += (tokens.output || 0) + (tokens.reasoning || 0);
      this.price(event.part?.cost);
      if (this.step.length) this.answer = this.step.join("\n");
    }
    if (event.type === "error") this.fail(event.error?.data?.message || event.error?.name || "error");
  }

  outcome() {
    return this.exitCode === 0 && !this.error && !this.incomplete() ? "completed" : "failed";
  }

  incomplete() {
    if (this.exitCode !== 0 || this.error) return null;   // The exit code or the error says why.
    if (!this.requests) return "OpenCode exited without any step_finish event: its output was not understood.";
    if (this.lastReason === "length") return "OpenCode's answer was cut off: the model reached its output limit (reason length).";
    if (this.lastReason !== "stop") return "OpenCode ended without a final answer (last step reason " + this.lastReason + ").";
    return null;
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

const STOPPED_ERRORS = {
  output_limit: "Stopped: the agent's output went past the plugin's limit.",
};

/**
 * The summary of a task. stopped is "cancelled", "timed_out", "paid" or "output_limit" when the plugin ended it;
 * tokens are then unknown. A task stopped for a paid request or too much output failed.
 */
export function summarise(reader, exitCode, stopped) {
  reader.exitCode = exitCode;
  const failedByPlugin = stopped === "paid" || stopped === "output_limit";
  const tokensKnown = !stopped && reader.usageSeen;
  return {
    outcome: failedByPlugin ? "failed" : stopped || reader.outcome(),
    requests: reader.requests,
    rate_limited: reader.rateLimited,
    input_tokens: tokensKnown ? reader.inputTokens : null,
    output_tokens: tokensKnown ? reader.outputTokens : null,
    cost: reader.cost,
    paid_request: reader.paid,
    byok_fallback: reader.byok,
    error:
      stopped === "paid" ? "Stopped: a request was priced as paid (" + reader.cost + "), so the free route was not used."
      : STOPPED_ERRORS[stopped] || reader.error || (stopped ? null : reader.incomplete()),
    answer: reader.answer,
  };
}

function textOf(content) {
  if (typeof content === "string") return content;
  if (!Array.isArray(content)) return "";
  return content.filter((part) => part.type === "text" && part.text).map((part) => part.text).join("\n");
}
