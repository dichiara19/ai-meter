// AI Meter's MCP server, reached through the relay AI Meter writes (mcp.ps1), the same way connected apps reach it.
// From Windows the relay runs in Windows PowerShell; from WSL, through interop. AI Meter must be installed; the relay
// starts it when it is not running. AI_METER_RELAY overrides the command (a JSON array), for tests.

import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { VERSION } from "./version.mjs";

// The GitHub copy writes its relay to %APPDATA%\AIMeter, the Store copy to ~\.aimeter; each rewrites its own at every
// start. A copy that was uninstalled leaves its relay behind, naming a program that is gone, so the newest one wins.
const RELAY_SCRIPT =
  "$p = @((Join-Path $env:APPDATA 'AIMeter\\mcp.ps1'), (Join-Path $env:USERPROFILE '.aimeter\\mcp.ps1')) | " +
  "Where-Object { Test-Path $_ } | Sort-Object { (Get-Item $_).LastWriteTimeUtc } -Descending | Select-Object -First 1; " +
  "if (-not $p) { [Console]::Error.WriteLine('AI Meter is not installed: no relay found.'); exit 1 }; " +
  "& $p";

/**
 * Where this script runs: "windows", "wsl", or null elsewhere (delegation needs AI Meter on Windows).
 * AI_METER_PLACE overrides the detection, for tests on plain Linux, which has neither.
 */
export function place() {
  if (process.env.AI_METER_PLACE === "windows" || process.env.AI_METER_PLACE === "wsl") return process.env.AI_METER_PLACE;
  if (process.platform === "win32") return "windows";
  if (process.platform !== "linux") return null;
  if (process.env.WSL_DISTRO_NAME) return "wsl";
  try {
    return /microsoft/i.test(readFileSync("/proc/version", "utf8")) ? "wsl" : null;
  } catch {
    return null;
  }
}

function relayCommand() {
  if (process.env.AI_METER_RELAY) return JSON.parse(process.env.AI_METER_RELAY);
  const where = place();
  const powershell =
    where === "windows"
      ? `${process.env.SystemRoot || "C:\\Windows"}\\System32\\WindowsPowerShell\\v1.0\\powershell.exe`
      : "/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe";
  if (where === "wsl" && !existsSync(powershell)) throw new Error("Windows interop is not available in this WSL distribution.");
  return [powershell, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", RELAY_SCRIPT];
}

/** An error a tool answered, such as describe_dispatch's "no key of its own"; the session itself still works. */
export class ToolError extends Error {}

const STILL_READING = /still reading the first data/i;

// Every request has a deadline, so a relay that stays up without answering cannot hold a job in "starting". The
// first one also covers starting AI Meter. An answer longer than the limit ends the session.
export const REQUEST_TIMEOUT_MS = Number(process.env.AI_METER_REQUEST_TIMEOUT_MS) || 30000;
const OPEN_TIMEOUT_MS = 90000;
const ANSWER_LIMIT_BYTES = 8 * 1024 * 1024;

/**
 * A session with AI Meter. call(tool, args) returns the tool's structured result, or throws with the tool's error.
 * Answers are matched to requests by id. An aborted signal, a closed relay or an over-long answer fails every
 * request still waiting and ends the relay.
 */
export class Meter {
  static async open(signal) {
    const meter = new Meter(signal);
    try {
      await meter.request("initialize", { protocolVersion: "2025-06-18", capabilities: {}, clientInfo: { name: "ai-meter-delegate", version: VERSION } }, OPEN_TIMEOUT_MS);
      meter.notify("notifications/initialized");
      return meter;
    } catch (error) {
      meter.close();
      throw error;
    }
  }

  constructor(signal) {
    const [program, ...args] = relayCommand();
    this.child = spawn(program, args, { stdio: ["pipe", "pipe", "pipe"], windowsHide: true });
    this.nextId = 1;
    this.waiting = new Map();
    this.buffer = "";
    this.stderr = "";
    this.child.stdout.setEncoding("utf8");
    this.child.stdout.on("data", (chunk) => this.receive(chunk));
    this.child.stderr.setEncoding("utf8");
    this.child.stderr.on("data", (chunk) => (this.stderr = (this.stderr + chunk).slice(-4000)));
    this.child.stdin.on("error", () => {});   // A relay that exits early closes its stdin; the exit says why.
    this.child.on("error", (error) => this.fail(error));
    this.child.on("exit", () => this.fail(new Error("AI Meter's relay closed. " + this.stderr.trim())));
    this.signal = signal;
    if (signal) {
      this.onAbort = () => this.fail(signal.reason instanceof Error ? signal.reason : new Error(String(signal.reason)));
      if (signal.aborted) queueMicrotask(this.onAbort);
      else signal.addEventListener("abort", this.onAbort, { once: true });
    }
  }

  receive(chunk) {
    this.buffer += chunk;
    if (this.buffer.length > ANSWER_LIMIT_BYTES && !this.buffer.includes("\n")) {
      this.fail(new Error("AI Meter's answer is longer than " + ANSWER_LIMIT_BYTES + " characters."));
      return;
    }
    let index;
    while ((index = this.buffer.indexOf("\n")) >= 0) {
      const line = this.buffer.slice(0, index).trim();
      this.buffer = this.buffer.slice(index + 1);
      if (!line) continue;
      let message;
      try {
        message = JSON.parse(line);
      } catch {
        continue;   // Not an answer; a request still waiting meets its deadline.
      }
      const pending = this.waiting.get(message?.id);
      if (!pending) continue;
      this.waiting.delete(message.id);
      clearTimeout(pending.timer);
      if (message.error) pending.reject(new Error(message.error.message));
      else pending.resolve(message.result);
    }
  }

  /** Fail every waiting request and end the relay: the session cannot be trusted to match answers any more. */
  fail(error) {
    for (const pending of this.waiting.values()) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    this.waiting.clear();
    this.close();
  }

  request(method, params, timeoutMs = REQUEST_TIMEOUT_MS) {
    if (this.signal?.aborted) return Promise.reject(this.signal.reason);
    return new Promise((resolve, reject) => {
      const id = this.nextId++;
      const timer = setTimeout(() => this.fail(new Error("AI Meter did not answer " + method + " within " + timeoutMs / 1000 + " s.")), timeoutMs);
      this.waiting.set(id, { resolve, reject, timer });
      this.child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
    });
  }

  notify(method) {
    this.child.stdin.write(JSON.stringify({ jsonrpc: "2.0", method }) + "\n");
  }

  /**
   * A just-started AI Meter answers only { status }, or a "still reading" error, until its first reading; wait for it,
   * up to a minute. Any other tool error throws a ToolError.
   */
  async call(name, args = {}) {
    for (let attempt = 0; ; attempt++) {
      const result = await this.request("tools/call", { name, arguments: args });
      const content = result.structuredContent || {};
      const error = result.isError || content.error ? content.error || "AI Meter refused " + name + "." : null;
      const starting = error ? STILL_READING.test(error) : Object.keys(content).length === 1 && typeof content.status === "string";
      if (!starting) {
        if (error) throw new ToolError(error);
        return content;
      }
      if (attempt === 20) throw new Error(error || content.status);
      await new Promise((resolve) => setTimeout(resolve, 3000));
      if (this.signal?.aborted) throw this.signal.reason;
    }
  }

  /** End the relay: stdin first, so it exits on its own, then the process, in case it does not. */
  close() {
    if (this.closed) return;
    this.closed = true;
    if (this.onAbort) this.signal.removeEventListener("abort", this.onAbort);
    this.child.stdin.end();
    setTimeout(() => this.child.exitCode === null && this.child.kill(), 2000).unref();
  }
}

/** Run fn with an open session, closing it afterwards. An aborted signal fails the session's waiting requests. */
export async function withMeter(fn, signal) {
  const meter = await Meter.open(signal);
  try {
    return await fn(meter);
  } finally {
    meter.close();
  }
}
