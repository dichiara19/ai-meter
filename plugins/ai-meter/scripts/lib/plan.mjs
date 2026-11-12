// The pay-as-you-go guard for agents on subscription plans (ADR 0003), the plugin's part of it:
//   - a clean environment: a plan agent starts with only what a program needs to run (system folders, the home, the
//     PATH, a temporary folder), never with the coordinator's environment. API keys, base URLs, cloud-provider switches
//     and the coordinator's own session variables cannot reach it, so it can only use its own plan sign-in;
//   - the sign-in check AI Meter describes (sign_in_check): the agent's own status command, run with that same
//     environment before the agent starts, must show a plan sign-in. Only the one field or text is looked at; the rest
//     of the output (an account's email, for example) is dropped and never stored;
//   - which service the coordinator itself runs on, so that AI Meter never offers its own plan.

import { spawn } from "node:child_process";

/** Variables a program needs to start and find its files, by platform. Everything else is left out. */
const WINDOWS_KEEP = [
  "SystemRoot", "SystemDrive", "windir", "ComSpec", "PATHEXT", "TEMP", "TMP", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA",
  "LOCALAPPDATA", "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)",
  "USERNAME", "USERDOMAIN", "COMPUTERNAME", "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS",
];
const POSIX_KEEP = ["HOME", "USER", "LOGNAME", "SHELL", "LANG", "LC_ALL", "LC_CTYPE", "TERM", "TMPDIR"];

/**
 * Variables that could make an agent bill an API instead of a plan, or tie it to the coordinator's session. They are
 * never kept, even when the agent's description or the keep lists above name them: a second line of defence.
 */
const NEVER = /^(ANTHROPIC_|CLAUDE_CODE_|CLAUDECODE$|CLAUDE_CONFIG_DIR$|OPENAI_|CODEX_|AZURE_|AWS_|GOOGLE_|GEMINI_|VERTEX|BEDROCK|OPENROUTER_|LITELLM_|HTTPS?_PROXY$|ALL_PROXY$|NODE_OPTIONS$|WSLENV$)/i;

/**
 * The environment a plan agent starts with: the variables of the keep list found in base (this process's
 * environment), the PATH (on Windows only the system folder: the agent is started by its full path), and the agent's
 * own variables from its description, minus anything NEVER matches.
 */
export function cleanEnvironment(extra = {}, base = process.env, platform = process.platform) {
  const keep = platform === "win32" ? WINDOWS_KEEP : POSIX_KEEP;
  const env = {};
  for (const [name, value] of Object.entries(base)) {
    if (keep.some((k) => k.toLowerCase() === name.toLowerCase())) env[name] = value;
  }
  const systemRoot = Object.entries(base).find(([name]) => name.toLowerCase() === "systemroot")?.[1];
  env.PATH = platform === "win32" ? (systemRoot ? systemRoot + "\\System32" : "") : "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
  for (const [name, value] of Object.entries(extra || {})) env[name] = value;
  for (const name of Object.keys(env)) if (NEVER.test(name)) delete env[name];
  return env;
}

/** The variables of an environment that the guard leaves out, by name only: for the tests and the report. */
export const isBlocked = (name) => NEVER.test(name);

/**
 * Run AI Meter's sign_in_check: its program and arguments, with env, without a shell, for at most timeoutMs. Resolves
 * to { ok: true } or { ok: false, reason }. The output is read only for the expected field or text.
 */
export function checkSignIn(check, env, timeoutMs = 30000) {
  return new Promise((resolve) => {
    if (!check?.program || !Array.isArray(check.arguments)) return resolve({ ok: false, reason: "AI Meter described no sign-in check for this plan agent" });
    let out = "";
    let err = "";
    let done = false;
    const finish = (result) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      resolve(result);
    };
    let child;
    try {
      child = spawn(check.program, check.arguments, { env, stdio: ["ignore", "pipe", "pipe"], windowsHide: true });
    } catch (error) {
      return finish({ ok: false, reason: "the sign-in check could not start: " + error.message });
    }
    const timer = setTimeout(() => {
      child.kill();
      finish({ ok: false, reason: "the sign-in check did not answer within " + timeoutMs / 1000 + " s" });
    }, timeoutMs);
    child.stdout.setEncoding("utf8").on("data", (chunk) => (out = (out + chunk).slice(-65536)));
    child.stderr.setEncoding("utf8").on("data", (chunk) => (err = (err + chunk).slice(-65536)));
    child.on("error", (error) => finish({ ok: false, reason: "the sign-in check could not start: " + error.message }));
    child.on("close", () => finish(judge(check, out, err)));
  });
}

/** Whether a sign-in check's output shows a plan sign-in. Codex writes its status to stderr, Claude Code to stdout. */
export function judge(check, stdout, stderr) {
  if (check.json_field) {
    let value;
    try {
      value = JSON.parse(stdout)[check.json_field];
    } catch {
      return { ok: false, reason: "the sign-in check did not answer in JSON" };
    }
    if (value === check.expect) return { ok: true };
    return { ok: false, reason: `the agent is signed in with ${typeof value === "string" ? value : "no plan"}, not ${check.expect}: it could bill an API, so it was not started` };
  }
  if (check.text && (stdout + "\n" + stderr).includes(check.text)) return { ok: true };
  return { ok: false, reason: `the sign-in check did not say "${check.text}": the agent may be signed in with an API key, so it was not started` };
}

/**
 * The service a coordinator runs on, from the client name it gave the plugin's MCP server: Claude Code runs on the
 * Claude plan, Codex on the ChatGPT one (service openai). Null when the client is not one of them.
 */
export function coordinatorOf(client) {
  const name = String(client || "").toLowerCase();
  if (name.startsWith("claude")) return "claude";
  if (name.includes("codex")) return "openai";
  return null;
}
