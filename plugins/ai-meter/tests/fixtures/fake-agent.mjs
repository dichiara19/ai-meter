// A stand-in for Pi or OpenCode: reads the task from stdin, then writes the agent's JSONL events.
// Usage: fake-agent.mjs <pi|opencode> <behaviour>, where behaviour is
//   ok        answers "echo: <task>";
//   error429  fails with a rate limit;
//   byok      (Pi) fails as OpenRouter does when a :free request fell back to a linked provider key;
//   unsettled (Pi) exits before agent_settled;
//   length    (Pi) ends with stopReason length, an answer cut off;
//   longline  (Pi) writes one line longer than the plugin's limit before its events, and its last event without a
//             newline;
//   list      (Pi) answers with the files in its folder and their contents, as JSON;
//   write     (OpenCode) changes a.txt and writes made-by-agent.txt in its folder, then answers;
//   commit    (OpenCode) like write, but commits a.txt in its worktree first;
//   silent    (OpenCode) exits with 0 without any event;
//   unfinished (OpenCode) exits with 0 after a step that ended with reason tool-calls;
//   paid      prices its first request above zero, then waits until it is stopped;
//   priced    prices its request above zero and ends normally, as on a free tier within its budget;
//   hang      waits until it is stopped;
//   family    starts a child process that waits, writes its pid to FAKE_CHILD_PID_FILE, then waits until stopped;
//   holdopen  (Pi) answers as ok, leaving behind a process that keeps a file in its folder open for four seconds, as
//             WSL keeps a Windows folder open for a while after an agent there exits;
//   tools     uses tools before answering, as in a live run: a command that fails, a write, an edit that fails (OpenCode)
//             and a command that succeeds with long output, each with absolute paths in its folder;
//   manytools (Pi) makes 45 tool calls before answering;
//   retried   (Pi) meets a 429, retries as Pi does, then answers normally.
// Requests are otherwise priced at zero, as the agents do for free models.

import { execFileSync, spawn } from "node:child_process";
import { readdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const [runtime, behaviour] = process.argv.slice(2);
const emit = (event) => process.stdout.write(JSON.stringify(event) + "\n");
// File contents an agent writes, which the report must never carry, and a command's long output.
const SECRET_CONTENT = "contents-the-report-must-not-carry";
const longOutput = () => "✔ case\n".repeat(200) + "ℹ tests 8\nℹ pass 8\nℹ fail 0\n";

let task = "";
process.stdin.setEncoding("utf8");
for await (const chunk of process.stdin) task += chunk;   // Ends only when the plugin closes stdin.

const cost = behaviour === "paid" || behaviour === "priced" ? 0.0021 : 0;
if (behaviour === "hang") setInterval(() => {}, 1000);
else if (behaviour === "family") family();
else if (runtime === "pi") pi();
else openCode();

function family() {
  const child = spawn(process.execPath, ["-e", "setInterval(() => {}, 1000)"], { stdio: "ignore" });
  writeFileSync(process.env.FAKE_CHILD_PID_FILE, String(child.pid));
  emit({ type: "step_start" });
  setInterval(() => {}, 1000);
}

function pi() {
  if (behaviour === "holdopen") {
    const holder = "const fs = require('node:fs'); const fd = fs.openSync('held.txt', 'w'); setTimeout(() => fs.closeSync(fd), 4000);";
    spawn(process.execPath, ["-e", holder], { detached: true, stdio: "ignore" }).unref();
  }
  if (behaviour === "longline") process.stdout.write("x".repeat(5 * 1024 * 1024) + "\n");
  emit({ type: "session", id: "s1", cwd: process.cwd() });
  emit({ type: "agent_start" });
  emit({ type: "turn_start" });
  if (behaviour === "tools") piTools();
  if (behaviour === "manytools") for (let i = 0; i < 45; i++) piTool("bash", { command: "echo " + i }, "" + i, false);
  if (behaviour === "retried") {
    const body = '429: {"message":"Provider returned error","code":429,"metadata":{"raw":"a:free is temporarily rate-limited upstream. Please retry shortly.","is_byok":false}}';
    emit({ type: "message_end", message: { role: "assistant", content: [], stopReason: "error", errorMessage: body, usage: { input: 0, output: 0, cost: { total: 0 } } } });
    emit({ type: "turn_end" });
    emit({ type: "auto_retry_start", attempt: 1, maxAttempts: 3, delayMs: 2000, errorMessage: body });
    emit({ type: "auto_retry_end", success: true, attempt: 1 });
  }
  const failed = behaviour === "error429" || behaviour === "byok";
  const byok = '402: {"message":"Provider returned error","code":402,"metadata":{"provider_name":"Google AI Studio","is_byok":true,' +
    '"previous_errors":[{"code":429,"message":"Provider returned error"}]}}';
  emit({
    type: "message_end",
    message: {
      role: "assistant",
      content: failed ? [] : [{ type: "text", text: behaviour === "list" ? listFolder() : "echo: " + task }],
      stopReason: failed ? "error" : behaviour === "length" ? "length" : "stop",
      errorMessage: behaviour === "byok" ? byok : failed ? "429 Too Many Requests: free-models-per-min" : undefined,
      usage: { input: 100, output: 7, cacheRead: 20, cacheWrite: 0, totalTokens: 127, cost: { total: cost } },
    },
  });
  emit({ type: "turn_end" });
  if (behaviour === "paid") return setInterval(() => {}, 1000);
  if (behaviour === "unsettled") return;
  emit({ type: "agent_end" });
  if (behaviour === "longline") process.stdout.write(JSON.stringify({ type: "agent_settled", aborted: false }));
  else emit({ type: "agent_settled", aborted: false });
}

/** One Pi tool call: its start and its end, with the result as Pi gives it. */
function piTool(toolName, args, output, isError) {
  const toolCallId = "call_" + Math.random().toString(16).slice(2);
  emit({ type: "tool_execution_start", toolCallId, toolName, args });
  emit({ type: "tool_execution_end", toolCallId, toolName, result: { content: [{ type: "text", text: output }] }, isError, durationMs: 5 });
}

function piTools() {
  const here = process.cwd();
  piTool("bash", { command: `cd ${here} && ls *.json`, timeout: 10 }, "ls: cannot access '*.json': No such file or directory\n\n\nCommand exited with code 2", true);
  writeFileSync("slug.test.js", SECRET_CONTENT);
  piTool("write", { path: join(here, "slug.test.js"), content: SECRET_CONTENT }, "Successfully wrote to " + join(here, "slug.test.js"), false);
  piTool("bash", { command: `cd ${here} && node --test 2>&1`, timeout: 60 }, longOutput(), false);
}

function openCodeTools() {
  const here = process.cwd();
  const tool = (callID, tool, state) => emit({ type: "tool_use", part: { id: "prt_" + callID, callID, type: "tool", tool, state } });
  tool("c1", "bash", { status: "completed", input: { command: `ls ${here}/*.json`, description: "List" }, output: "ls: cannot access\n", metadata: { exit: 2 } });
  writeFileSync("slug.test.js", SECRET_CONTENT);
  tool("c2", "write", { status: "completed", input: { filePath: join(here, "slug.test.js"), content: SECRET_CONTENT }, output: "" });
  tool("c3", "edit", { status: "error", input: { filePath: join(here, "slug.js"), oldString: "x", newString: "y" }, error: "oldString not found in content" });
  tool("c4", "bash", { status: "completed", input: { command: "node --test" }, output: longOutput(), metadata: { exit: 0 } });
}

function openCode() {
  if (behaviour === "silent") return;
  if (behaviour === "write" || behaviour === "commit") {
    writeFileSync("a.txt", "changed by the agent\n");
    if (behaviour === "commit") {
      execFileSync("git", ["add", "a.txt"]);
      execFileSync("git", ["-c", "user.email=agent@test", "-c", "user.name=agent", "commit", "-qm", "agent change"]);
    }
    writeFileSync("made-by-agent.txt", process.env.FAKE_AGENT_MODE + "\n");
  }
  emit({ type: "step_start" });
  if (behaviour === "error429") {
    emit({ type: "error", error: { name: "APIError", data: { message: "Provider returned error 429" } } });
    process.exit(1);
  }
  if (behaviour === "tools") openCodeTools();
  emit({ type: "tool_use", part: { tool: "read", state: { status: "completed" } } });
  emit({ type: "step_finish", part: { reason: "tool-calls", cost, tokens: { input: 50, output: 5, reasoning: 1, cache: { read: 10, write: 0 } } } });
  if (behaviour === "paid") return setInterval(() => {}, 1000);
  if (behaviour === "unfinished") return;
  emit({ type: "step_start" });
  emit({ type: "text", part: { text: "echo: " + task } });
  emit({ type: "step_finish", part: { reason: "stop", cost, tokens: { input: 60, output: 9, reasoning: 0, cache: { read: 0, write: 0 } } } });
}

function listFolder() {
  const files = {};
  for (const path of readdirSync(".", { recursive: true })) {
    const name = path.replaceAll("\\", "/");
    try {
      files[name] = readFileSync(path, "utf8");
    } catch {
      // A folder.
    }
  }
  return JSON.stringify(files);
}
