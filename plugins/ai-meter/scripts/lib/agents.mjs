// Reading a coding agent's JSONL events, as describe_dispatch's `output` explains them for each runtime
// (docs/plans/spike-agents.md). A reader takes one parsed event at a time and, at the end, sums up the task for
// release_capacity and for the coordinator: outcome, requests, tokens, rate limits, cost, the agent's final answer, and
// what it did on the way (its tool calls, files written and commands run: activity.mjs), as evidence for the answer.
//
// A task is completed only on positive evidence: the agent's own final event with a normal end. An answer cut off
// for length, an unknown end, or output with none of the expected events is a failure with the reason, never a
// success with an empty answer. Tokens are null, not zero, when no event reported any. Errors the agent recovered from
// (a 429 that Pi retried, for example) do not make a completed task look failed: they are reported apart, as
// recovered_errors, and `error` is kept for tasks that did not complete.
//
// Cost: each agent prices its requests from its own model list. A request priced above zero was paid, so `paid`
// tells the plugin to stop the agent; a zero price does not prove the request was free (an OpenRouter BYOK fallback
// is billed by the provider, out of the agent's sight). OpenRouter does name a BYOK provider in its error bodies
// ("is_byok":true), so an error that shows one is reported as byok_fallback.
// A free tier (Cloudflare Workers AI) is the exception: its requests are priced, and free only within a daily
// allocation. AI Meter's claim_capacity grants such a task a budget in US dollars (allowance); `paid` then means the
// requests cost more than that budget, beyond which they would be billed or refused.

import { Activity } from "./activity.mjs";

/** Shared counters; the readers below add to them. */
class Reader {
  /** folderName: the name of the task's folder, to shorten the paths in activity. */
  constructor(folderName) {
    this.requests = 0;
    this.inputTokens = 0;
    this.outputTokens = 0;
    this.rateLimited = 0;
    this.cost = 0;
    this.allowance = 0;   // US dollars the requests may cost: 0 for free models, a free tier's budget otherwise.
    this.paid = false;
    this.error = null;
    this.errors = 0;
    this.byok = false;
    this.answer = "";
    this.usageSeen = false;
    this.activity = new Activity(folderName);
  }

  /** Why the run did not end normally, or null when it did. */
  incomplete() {
    return null;
  }

  fail(message) {
    this.error = message;
    this.errors++;
    if (/\b429\b/.test(message)) this.rateLimited++;
    if (/"is_byok"\s*:\s*true/.test(message)) this.byok = true;
  }

  price(cost) {
    if (typeof cost !== "number" || !(cost > 0)) return;
    this.cost += cost;
    if (this.cost > this.allowance) this.paid = true;
  }
}

/** Pi 1.x in JSON mode. The exit code stays 0 on errors, so the outcome is the last assistant stopReason. */
class PiReader extends Reader {
  constructor(folderName) {
    super(folderName);
    this.settled = false;
    this.stopReason = null;
  }

  feed(event) {
    if (event.type === "turn_end") this.requests++;
    if (event.type === "agent_settled") this.settled = true;
    if (event.type === "tool_execution_start") this.activity.begin(event.toolCallId, event.toolName, event.args);
    if (event.type === "tool_execution_end") this.activity.end(event.toolCallId, { failed: event.isError === true, output: textOf(event.result?.content) });
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
  constructor(folderName) {
    super(folderName);
    this.step = [];
    this.exitCode = null;
    this.lastReason = null;
    this.calls = 0;
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
    if (event.type === "tool_use") this.tool(event.part || {});
    if (event.type === "error") this.fail(event.error?.data?.message || event.error?.name || "error");
  }

  /**
   * A tool_use part: the tool, and its state with status, input and, once ended, output (or error) and, for bash,
   * metadata.exit. OpenCode reports a call when it ends; a pending or running state starts it, should one come first.
   */
  tool(part) {
    const id = part.callID ?? part.id ?? "call-" + ++this.calls;
    const state = part.state || {};
    this.activity.begin(id, part.tool, state.input);
    if (state.status !== "completed" && state.status !== "error") return;
    const exit = state.metadata?.exit;
    this.activity.end(id, {
      failed: state.status === "error" || (Number.isInteger(exit) && exit !== 0),
      output: state.status === "error" ? state.error : state.output ?? state.metadata?.output,
      exitCode: exit,
    });
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

/** A reader for a runtime's events. folderName: the name of the task's folder, to shorten paths in activity. */
export function readerFor(runtime, folderName) {
  if (!canRead(runtime)) throw new Error("No event reader for " + runtime + ".");
  return new READERS[runtime](folderName);
}

const STOPPED_ERRORS = {
  output_limit: "Stopped: the agent's output went past the plugin's limit.",
};

/**
 * The summary of a task. stopped is "cancelled", "timed_out", "paid" or "output_limit" when the plugin ended it;
 * tokens are then unknown. A task stopped for a paid request or too much output failed. A completed task has no
 * error: the errors its agent recovered from are counted in recovered_errors, with the last one shortened.
 */
export function summarise(reader, exitCode, stopped) {
  reader.exitCode = exitCode;
  const failedByPlugin = stopped === "paid" || stopped === "output_limit";
  const tokensKnown = !stopped && reader.usageSeen;
  const outcome = failedByPlugin ? "failed" : stopped || reader.outcome();
  const completed = outcome === "completed";
  return {
    outcome,
    requests: reader.requests,
    rate_limited: reader.rateLimited,
    input_tokens: tokensKnown ? reader.inputTokens : null,
    output_tokens: tokensKnown ? reader.outputTokens : null,
    cost: reader.cost,
    ...(reader.allowance > 0 ? { allowance: reader.allowance } : {}),
    paid_request: reader.paid,
    byok_fallback: reader.byok,
    error:
      completed ? null
      : stopped === "paid" && reader.allowance > 0 ? "Stopped: the requests cost " + reader.cost + " USD, past the free allocation's budget of " + reader.allowance + " USD."
      : stopped === "paid" ? "Stopped: a request was priced as paid (" + reader.cost + "), so the free route was not used."
      : STOPPED_ERRORS[stopped] || reader.error || (stopped ? null : reader.incomplete()),
    ...(completed && reader.errors ? { recovered_errors: { count: reader.errors, last: shortError(reader.error) } } : {}),
    answer: reader.answer,
    activity: reader.activity.report(),
  };
}

/**
 * An agent's error in one short line. Providers answer with a JSON body after the status code, as OpenRouter's
 * "429: {"message":…,"metadata":{"raw":…}}"; its readable part is the provider's own message, when there is one.
 */
export function shortError(message) {
  let text = String(message || "");
  const body = /^(\d{3}):\s*(\{[\s\S]*\})\s*$/.exec(text);
  if (body) {
    try {
      const parsed = JSON.parse(body[2]);
      const said = parsed.metadata?.raw || parsed.error?.message || parsed.message;
      if (typeof said === "string" && said) text = body[1] + ": " + said;
    } catch {
      // Not JSON after all: kept as it is.
    }
  }
  text = text.replace(/\s+/g, " ").trim();
  return text.length > 300 ? text.slice(0, 299) + "…" : text;
}

function textOf(content) {
  if (typeof content === "string") return content;
  if (!Array.isArray(content)) return "";
  return content.filter((part) => part.type === "text" && part.text).map((part) => part.text).join("\n");
}
