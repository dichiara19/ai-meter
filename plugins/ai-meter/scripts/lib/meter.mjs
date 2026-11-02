// AI Meter's MCP server, reached through the relay AI Meter writes (mcp.ps1), the same way connected apps reach it.
// From Windows the relay runs in Windows PowerShell; from WSL, through interop. AI Meter must be installed; the relay
// starts it when it is not running. AI_METER_RELAY overrides the command (a JSON array), for tests.

import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";

const RELAY_SCRIPT =
  "$p = Join-Path $env:APPDATA 'AIMeter\\mcp.ps1'; " +
  "if (-not (Test-Path $p)) { $p = Join-Path $env:USERPROFILE '.aimeter\\mcp.ps1' }; " +
  "& $p";

/** Where this script runs: "windows", "wsl", or null elsewhere (delegation needs AI Meter on Windows). */
export function place() {
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

/**
 * A session with AI Meter. call(tool, args) returns the tool's structured result, or throws with the tool's error.
 * Requests are answered in order, one line each, so a simple queue matches answers to requests.
 */
export class Meter {
  static async open() {
    const meter = new Meter();
    await meter.request("initialize", { protocolVersion: "2025-06-18", capabilities: {}, clientInfo: { name: "ai-meter-delegate", version: "0.1.0" } });
    meter.notify("notifications/initialized");
    return meter;
  }

  constructor() {
    const [program, ...args] = relayCommand();
    this.child = spawn(program, args, { stdio: ["pipe", "pipe", "pipe"], windowsHide: true });
    this.nextId = 1;
    this.waiting = [];
    this.buffer = "";
    this.stderr = "";
    this.child.stdout.setEncoding("utf8");
    this.child.stdout.on("data", (chunk) => this.receive(chunk));
    this.child.stderr.on("data", (chunk) => (this.stderr += chunk));
    this.child.on("error", (error) => this.fail(error));
    this.child.on("exit", () => this.fail(new Error("AI Meter's relay closed. " + this.stderr.trim())));
  }

  receive(chunk) {
    this.buffer += chunk;
    let index;
    while ((index = this.buffer.indexOf("\n")) >= 0) {
      const line = this.buffer.slice(0, index).trim();
      this.buffer = this.buffer.slice(index + 1);
      if (!line) continue;
      const pending = this.waiting.shift();
      if (!pending) continue;
      try {
        const message = JSON.parse(line);
        if (message.error) pending.reject(new Error(message.error.message));
        else pending.resolve(message.result);
      } catch (error) {
        pending.reject(error);
      }
    }
  }

  fail(error) {
    for (const pending of this.waiting.splice(0)) pending.reject(error);
  }

  request(method, params) {
    return new Promise((resolve, reject) => {
      this.waiting.push({ resolve, reject });
      this.child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id: this.nextId++, method, params }) + "\n");
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
    }
  }

  close() {
    this.child.stdin.end();
  }
}

/** Run fn with an open session, closing it afterwards. */
export async function withMeter(fn) {
  const meter = await Meter.open();
  try {
    return await fn(meter);
  } finally {
    meter.close();
  }
}
