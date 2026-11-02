// Reading a coding agent's JSONL events, as describe_dispatch's `output` explains them for each runtime
// (docs/plans/spike-agents.md). A reader takes one parsed event at a time and, at the end, sums up the task for
// release_capacity and for the coordinator: outcome, requests, tokens, rate limits and the agent's final answer.

/** Pi 1.x in JSON mode. The exit code stays 0 on errors, so the outcome is the last assistant stopReason. */
class PiReader {
  constructor() {
    this.requests = 0;
    this.inputTokens = 0;
    this.outputTokens = 0;
    this.rateLimited = 0;
    this.settled = false;
    this.stopReason = null;
    this.error = null;
    this.answer = "";
  }

  feed(event) {
    if (event.type === "turn_end") this.requests++;
    if (event.type === "agent_settled") this.settled = true;
    if (event.type !== "message_end" || event.message?.role !== "assistant") return;

    const message = event.message;
    const usage = message.usage || {};
    this.inputTokens += (usage.input || 0) + (usage.cacheRead || 0) + (usage.cacheWrite || 0);
    this.outputTokens += usage.output || 0;
    this.stopReason = message.stopReason || this.stopReason;
    if (message.errorMessage) {
      this.error = message.errorMessage;
      if (/\b429\b/.test(message.errorMessage)) this.rateLimited++;
    }
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
class OpenCodeReader {
  constructor() {
    this.requests = 0;
    this.inputTokens = 0;
    this.outputTokens = 0;
    this.rateLimited = 0;
    this.error = null;
    this.step = [];
    this.answer = "";
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
      if (this.step.length) this.answer = this.step.join("\n");
    }
    if (event.type === "error") {
      this.error = event.error?.data?.message || event.error?.name || "error";
      if (/\b429\b/.test(this.error)) this.rateLimited++;
    }
  }

  outcome() {
    return this.exitCode === 0 && !this.error ? "completed" : "failed";
  }
}

/** A reader for the runtime describe_dispatch named; only verified runtimes are described, so others are refused. */
export function readerFor(runtime) {
  if (runtime === "pi") return new PiReader();
  if (runtime === "opencode") return new OpenCodeReader();
  throw new Error("No event reader for " + runtime + ".");
}

/** The summary of a task. stopped is "cancelled" or "timed_out" when the plugin ended it; tokens are then unknown. */
export function summarise(reader, exitCode, stopped) {
  reader.exitCode = exitCode;
  return {
    outcome: stopped || reader.outcome(),
    requests: reader.requests,
    rate_limited: reader.rateLimited,
    input_tokens: stopped ? null : reader.inputTokens,
    output_tokens: stopped ? null : reader.outputTokens,
    error: reader.error,
    answer: reader.answer,
  };
}

function textOf(content) {
  if (typeof content === "string") return content;
  if (!Array.isArray(content)) return "";
  return content.filter((part) => part.type === "text" && part.text).map((part) => part.text).join("\n");
}
