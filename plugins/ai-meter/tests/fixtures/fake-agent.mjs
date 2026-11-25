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
//   retried   (Pi) meets a 429, retries as Pi does, then answers normally;
//   nokey     (Pi) exits with 1 after its session event, the reason on stderr, as Pi does without a key;
//   leak      (Pi) prints auth.json in PI_CODING_AGENT_DIR with a command and quotes every key in its answer, as a
//             model told to could;
//   leakerror (Pi) writes auth.json's contents to stderr and exits with 1 before agent_settled;
//   leakcommit (Pi) writes a file named after the first key and commits it with the key in the subject, then answers;
//   rotate    (Pi) adds a new key to auth.json while it runs, as a user could in AI Meter, and quotes it;
//   breakkeys (Pi) quotes the first key, then leaves auth.json unreadable as JSON;
//   flip      (Pi) adds a key and withdraws it again, each change counted in keys-revision as AI Meter does, and
//             quotes it: a key that came and went between the plugin's two readings;
//   halfchange (Pi) adds a key and quotes it, announcing the change in keys-revision but never finishing it;
//   badrevision (Pi) quotes the first key and leaves keys-revision without a number;
//   breakgit  (Pi) like breakkeys, and also breaks its worktree's .git file, so that Git cannot describe it and its
//             error would name the key;
//   gitfails  (Pi) breaks its worktree's .git file in the same way, leaving the keys readable.
// Every change to auth.json is counted in keys-revision as AI Meter counts it: odd before the file is touched, even after.
// Requests are otherwise priced at zero, as the agents do for free models. A third argument (the managed Pi's shell)
// is named in Pi's answer: "echo via <shell>: <task>".
//
// Claude Code and Codex on a plan replay the events recorded live (claude-plan.jsonl, codex-plan.jsonl), with {folder}
// standing for their folder; FAKE_ENV_FILE, when set, receives the agent's environment as JSON. Behaviours:
//   ok        the recorded run;
//   apikey    (Claude Code) starts on an API key, then waits until it is stopped;
//   overage   (Claude Code) reports extra usage in use, then waits until it is stopped;
//   limit     the plan's limit is reached: Claude Code ends with an error result, Codex with turn.failed and exit 1.
// fake-agent.mjs signin <claude|codex> <plan|apikey> answers a sign-in check as the agent's status command does.

import { execFileSync, spawn } from "node:child_process";
import { existsSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const [runtime, behaviour, signIn, ...assignments] = process.argv.slice(2);
// NAME=value arguments after the shell stand for the variables wsl.exe -e env passes to the managed copy inside WSL.
// FAKE_ENV_FILE receives the environment the agent was started with, before these: what wsl.exe itself would get.
const started = { ...process.env };
for (const assignment of assignments) {
  const at = assignment.indexOf("=");
  if (at > 0) process.env[assignment.slice(0, at)] = assignment.slice(at + 1);
}
const emit = (event) => process.stdout.write(JSON.stringify(event) + "\n");
// File contents an agent writes, which the report must never carry, and a command's long output.
const SECRET_CONTENT = "contents-the-report-must-not-carry";
const longOutput = () => "✔ case\n".repeat(200) + "ℹ tests 8\nℹ pass 8\nℹ fail 0\n";

if (runtime === "signin") {
  // Claude Code's auth status answers in JSON on stdout (with the account, which the plugin must drop); Codex's login
  // status writes a line to stderr.
  if (behaviour === "claude") process.stdout.write(JSON.stringify({ loggedIn: true, authMethod: signIn === "plan" ? "claude.ai" : "api_key", email: "someone@example.invalid" }));
  else process.stderr.write(signIn === "plan" ? "Logged in using ChatGPT\n" : "Logged in using an API key - sk-proj-***test\n");
  process.exit(0);
}
if (process.env.FAKE_ENV_FILE) writeFileSync(process.env.FAKE_ENV_FILE, JSON.stringify(started));

let task = "";
process.stdin.setEncoding("utf8");
for await (const chunk of process.stdin) task += chunk;   // Ends only when the plugin closes stdin.

const cost = behaviour === "paid" || behaviour === "priced" ? 0.0021 : 0;
if (runtime === "claude" || runtime === "codex") plan();
else if (behaviour === "hang") setInterval(() => {}, 1000);
else if (behaviour === "family") family();
else if (runtime === "pi") pi();
else openCode();

/** A plan agent: the recorded run, or one of the endings it must be stopped or failed for. */
function plan() {
  const recorded = readFileSync(join(dirname(fileURLToPath(import.meta.url)), runtime + "-plan.jsonl"), "utf8")
    .trim().split("\n").map((line) => JSON.parse(line.split("{folder}").join(process.cwd().replaceAll("\\", "/"))));
  if (runtime === "claude") {
    const [init, ...rest] = recorded;
    if (behaviour === "apikey") {
      emit({ ...init, apiKeySource: "ANTHROPIC_API_KEY" });
      return setInterval(() => {}, 1000);
    }
    emit(init);
    if (behaviour === "overage") {
      emit({ type: "rate_limit_event", rate_limit_info: { status: "allowed", rateLimitType: "five_hour", overageStatus: "allowed", isUsingOverage: true } });
      return setInterval(() => {}, 1000);
    }
    if (behaviour === "limit") {
      emit({ type: "rate_limit_event", rate_limit_info: { status: "rejected", rateLimitType: "five_hour", isUsingOverage: false } });
      return emit({ type: "result", subtype: "success", is_error: true, num_turns: 1, result: "Claude AI usage limit reached", usage: { input_tokens: 3, output_tokens: 0 } });
    }
    for (const event of rest) emit(event);
    return;
  }
  if (behaviour === "limit") {
    emit(recorded[0]);
    emit({ type: "turn.started" });
    emit({ type: "turn.failed", error: { message: "You've hit your usage limit. Try again later." } });
    process.exit(1);
  }
  for (const event of recorded) emit(event);
}

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
  if (behaviour === "nokey") {
    // What Pi 1.5.2 writes on Windows: a warning first when the model is not in its list, pointers to its documentation last.
    process.stderr.write('Warning: Model "x" not found for provider "openrouter". Using custom model id.\nNo API key found for openrouter.\n\nUse /login to log into a provider via OAuth or API key. See:\n  docs/providers.md\n');
    process.exitCode = 1;
    return;
  }
  const authFile = join(process.env.PI_CODING_AGENT_DIR || ".", "auth.json");
  if (behaviour === "leakerror") {
    process.stderr.write("Could not parse " + readFileSync(authFile, "utf8") + "\n");
    process.exitCode = 1;
    return;
  }
  emit({ type: "agent_start" });
  emit({ type: "turn_start" });
  if (behaviour === "tools") piTools();
  let answer = behaviour === "list" ? listFolder() : (signIn ? "echo via " + signIn + ": " : "echo: ") + task;
  if (behaviour === "leak") {
    const auth = readFileSync(authFile, "utf8");
    piTool("bash", { command: "cat " + authFile }, auth, false);
    answer = "The keys are: " + Object.values(JSON.parse(auth)).map((entry) => entry.key).join(" and ") + ".";
  }
  if (["leakcommit", "rotate", "breakkeys", "flip", "halfchange", "badrevision", "breakgit", "gitfails"].includes(behaviour)) {
    const auth = JSON.parse(readFileSync(authFile, "utf8"));
    const first = Object.values(auth)[0].key;
    const revisionFile = join(process.env.PI_CODING_AGENT_DIR, "keys-revision");
    // As AI Meter changes the keys: the count goes to odd first, then the file changes, then the count goes to even.
    const count = () => (existsSync(revisionFile) ? Number(readFileSync(revisionFile, "utf8")) : 0);
    const changeKeys = (contents, finish = true) => {
      writeFileSync(revisionFile, String(count() + 1));
      writeFileSync(authFile, contents);
      if (finish) writeFileSync(revisionFile, String(count() + 1));
    };
    answer = "done";
    if (behaviour === "halfchange") {
      const added = "sk-added-but-not-finished-42";
      changeKeys(JSON.stringify({ ...auth, added: { type: "api_key", key: added } }), false);
      answer = "The added key is " + added + ".";
    }
    if (behaviour === "flip") {
      const passing = "sk-came-and-went-0123456789";
      changeKeys(JSON.stringify({ ...auth, passing: { type: "api_key", key: passing } }));
      changeKeys(JSON.stringify(auth));
      answer = "The passing key was " + passing + ".";
    }
    if (behaviour === "badrevision") {
      writeFileSync(revisionFile, "seven");
      answer = "The key is " + first + ".";
    }
    if (behaviour === "breakgit" || behaviour === "gitfails") {
      if (behaviour === "breakgit") changeKeys("{ broken");
      // Git's error then names this folder, and with it the key. Git for Windows makes .git hidden, which Windows does
      // not let a program overwrite, so it is removed first.
      rmSync(".git", { force: true });
      writeFileSync(".git","gitdir: " + join(process.cwd(), "no-such-git-folder-" + first));
      answer = "The key is " + first + ".";
    }
    if (behaviour === "leakcommit") {
      writeFileSync(first + ".txt", "x\n");
      execFileSync("git", ["add", "."]);
      execFileSync("git", ["-c", "user.email=agent@test", "-c", "user.name=agent", "commit", "-qm", "store " + first]);
    }
    if (behaviour === "rotate") {
      const rotated = "sk-rotated-during-the-task-42";
      changeKeys(JSON.stringify({ ...auth, fresh: { type: "api_key", key: rotated } }));
      answer = "The new key is " + rotated + ".";
    }
    if (behaviour === "breakkeys") {
      changeKeys("{ broken");
      answer = "The key is " + first + ".";
    }
  }
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
      content: failed ? [] : [{ type: "text", text: answer }],
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
