// A stand-in for Pi or OpenCode: reads the task from stdin, then writes the agent's JSONL events.
// Usage: fake-agent.mjs <pi|opencode> <behaviour>, where behaviour is
//   ok        answers "echo: <task>";
//   error429  fails with a rate limit;
//   unsettled (Pi) exits before agent_settled;
//   write     (OpenCode) changes a.txt and writes made-by-agent.txt in its folder, then answers;
//   hang      waits until it is stopped.

import { writeFileSync } from "node:fs";

const [runtime, behaviour] = process.argv.slice(2);
const emit = (event) => process.stdout.write(JSON.stringify(event) + "\n");

let task = "";
process.stdin.setEncoding("utf8");
for await (const chunk of process.stdin) task += chunk;   // Ends only when the plugin closes stdin.

if (behaviour === "hang") setInterval(() => {}, 1000);
else if (runtime === "pi") pi();
else openCode();

function pi() {
  emit({ type: "session", id: "s1", cwd: process.cwd() });
  emit({ type: "agent_start" });
  emit({ type: "turn_start" });
  const failed = behaviour === "error429";
  emit({
    type: "message_end",
    message: {
      role: "assistant",
      content: failed ? [] : [{ type: "text", text: "echo: " + task }],
      stopReason: failed ? "error" : "stop",
      errorMessage: failed ? "429 Too Many Requests: free-models-per-min" : undefined,
      usage: { input: 100, output: 7, cacheRead: 20, cacheWrite: 0, totalTokens: 127 },
    },
  });
  emit({ type: "turn_end" });
  if (behaviour === "unsettled") return;
  emit({ type: "agent_end" });
  emit({ type: "agent_settled", aborted: false });
}

function openCode() {
  if (behaviour === "write") {
    writeFileSync("a.txt", "changed by the agent\n");
    writeFileSync("made-by-agent.txt", process.env.FAKE_AGENT_MODE + "\n");
  }
  emit({ type: "step_start" });
  if (behaviour === "error429") {
    emit({ type: "error", error: { name: "APIError", data: { message: "Provider returned error 429" } } });
    process.exit(1);
  }
  emit({ type: "tool_use", part: { tool: "read", state: { status: "completed" } } });
  emit({ type: "step_finish", part: { reason: "tool-calls", tokens: { input: 50, output: 5, reasoning: 1, cache: { read: 10, write: 0 } } } });
  emit({ type: "step_start" });
  emit({ type: "text", part: { text: "echo: " + task } });
  emit({ type: "step_finish", part: { reason: "stop", tokens: { input: 60, output: 9, reasoning: 0, cache: { read: 0, write: 0 } } } });
}
