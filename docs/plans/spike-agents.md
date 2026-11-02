# Spike: running coding agents without interaction (M4)

How a coordinator plugin can start a coding agent on a free model, pass the task, follow it, read the outcome and stop it. Live checks ran on Windows 11 with WSL Ubuntu 24.04, on 2026-11-02. Everything not run live is marked **not verified**.

The results feed `describe_dispatch` ([contracts §3.1](orchestration-contracts.md#31-describe_dispatch)) and the plugins (M5).

## Pi 1.4.0, in WSL (verified live)

**Start and task input.**
- `pi -p --mode json …` runs one task and exits. `-p` makes it non-interactive; `--mode json` writes JSONL events to stdout.
- **The task goes through stdin.** `printf … | pi -p --mode json …` with no message argument works. So the task never needs a shell string or a command-line argument. Verified.
- `--provider openrouter --model "<id>:free"` picks the model. `pi --list-models ":free"` lists them, with context, output limit and thinking support.
- **Isolation and quiet start.** `--no-session` keeps no session file. `--no-extensions --no-mcp --no-context-files` stop it loading the user's extensions, MCP servers and `AGENTS.md`/`CLAUDE.md`.
- `--tools read,ls` limits the tools. The Pi help gives `--tools read,grep,find,ls` as its read-only example.
- **Checking a key without seeing it.** `pi auth check --provider <id> --json` returns `{"status":"ready","provider":…,"authType":"api_key"}`, without the key unless `--credentials` is passed. It is an alternative to reading Pi's files, but it starts a process in WSL.

**Events** (JSONL, one per line), in this order:
1. `session` (id, cwd);
2. `agent_start`;
3. for each request to the model:
   - `turn_start`;
   - `message_start` / `message_update` / `message_end` (system, user, assistant);
   - `tool_execution_start` / `tool_execution_end` (tool name, arguments, result, `isError`);
   - `turn_end`;
4. `agent_end`;
5. `agent_settled` (`aborted`).

**Outcome.**
- **The exit code is 0 even when the model fails.** The failure is in the assistant message: `stopReason: "error"` with `errorMessage`. Pi documents that print mode (`-p` without `--mode json`) does exit non-zero on `error` or `aborted`. A plugin using JSON mode must read `stopReason` (`stop`, `toolUse`, `length`, `error`, `aborted`).
- **Requests.** One `turn_end` per request to the model. A read task (`ls`, then `read`, then the answer) made 3 requests.
- **Tokens.** Each assistant message carries `usage` (`input`, `output`, `cacheRead`, `cacheWrite`, `totalTokens`, `cost`). A one-word answer used 4,615 tokens, 4,582 of them input. **Pi's system prompt costs about 4.6k input tokens per request.** Savings measurements must count it.
- **Rate limits.** A `429` from the provider showed up inside `errorMessage`, not as its own event, so a plugin has to parse the message to report `rate_limited`.

**Stopping.**
- `SIGINT` gives exit 130, and `SIGTERM` gives 143, both at once. No final event is written and no Pi process is left behind.
- RPC mode (`pi --mode rpc --no-session …`, commands as JSONL on stdin) accepts `{"type":"abort"}`. It answered in 0.2 s, ended the turn with `stopReason: "aborted"` and `agent_settled.aborted: true`, and exited with 0 when stdin closed.
- **Either way, a stopped task reports 0 tokens.** The work done before stopping is not counted, so a cancelled lease releases with unknown tokens.

**A free model is not always free.**
- The first run, on `google/gemma-4-31b-it:free`, failed. OpenRouter got a `429` from the free route, then retried on a provider key linked to OpenRouter as BYOK, and that provider refused it with `402`.
- With BYOK keys linked in OpenRouter, a `:free` model can end up billed to the user's own provider account. `confirmed_free` comes from the catalogue price only, so it cannot see this. The plugin and the docs must warn about it. Spotting it would need OpenRouter's key settings, which AI Meter does not read today.

**Dispatch row (Pi 1.x, read-only task):**
- program `pi`;
- arguments `["-p", "--mode", "json", "--no-session", "--no-extensions", "--no-mcp", "--no-context-files", "--tools", "read,grep,find,ls", "--provider", "<service>", "--model", "<model>"]`;
- `task_input: "stdin"`;
- output `jsonl`: done when `agent_settled` arrives; the outcome is the last assistant `stopReason`, tokens are the sum of assistant `usage`, requests are the number of `turn_end`;
- stop with `SIGINT`, or `abort` in RPC mode;
- the working folder is the process's.

A task that changes files drops `--tools`, or lists the editing tools, and runs in a new worktree.

## Codex CLI 0.163.0, in WSL (flags verified, no free-model run)

- `codex exec` runs one task. The prompt is read from stdin when it is not given as an argument, or when the argument is `-`.
- Useful flags:
  - `--json` (JSONL events);
  - `-s read-only` / `workspace-write` (sandbox);
  - `-C <dir>` (working root);
  - `--ephemeral` (no session files);
  - `--skip-git-repo-check`;
  - `-o <file>` (last message);
  - `-c key=value` (configuration overrides, including `model_provider` and `model_providers.<id>`);
  - `--oss --local-provider ollama|lmstudio` (local models).
- **Free models.** Codex can run them only through a provider the user configured with its own key variable. On the test PC no such variable is exported in WSL, so a free OpenRouter run was not tried: AI Meter must not pass a key. Local models through `--oss` are a free route to check.
- **Not verified:** the event names in `--json`, the exit codes, and cancellation.

## OpenCode 1.19.5, in WSL (verified live)

Installed for the spike with `npm install -g opencode-ai@1.19.5`, with the maintainer's agreement; npm blocked its post-install script and the program works without it. The maintainer added the OpenRouter key with `opencode auth login` (`~/.local/share/opencode/auth.json`).

**Start and task input.**
- `opencode run --format json --dir <folder> -m <provider>/<model>` runs one task and exits.
- **The task goes through stdin**, with no message argument. **With stdin left open and no message, OpenCode waits forever**: the first try hung for 240 s. A plugin must write the task and close stdin.
- `--pure` drops external plugins only.
- **It still loads the user's skills** (from `~/.config/opencode`, `~/.claude/skills`, `~/.agents/skills`): 65 skills here. The one-word answer then cost **24,927 input tokens**.
- **Extra model calls.**
  - OpenCode makes a separate call to its "small model" to title the session. Here that was `google/gemini-nano-banana-2.1`, a paid model, and it failed: "This request requires at least $1.00 in balance".
  - **A delegated free task can therefore call a paid model on the side.**
- **Isolation, found in the program and checked by effect.** These environment variables are not all documented:

  ```
  OPENCODE_DISABLE_CLAUDE_CODE=1  OPENCODE_DISABLE_EXTERNAL_SKILLS=1
  OPENCODE_DISABLE_PROJECT_CONFIG=1  OPENCODE_DISABLE_AUTOUPDATE=1
  OPENCODE_CONFIG_CONTENT={"small_model":"<the same free model>"}
  ```

  With them: no skills loaded, the title call goes to the same free model, and the answer cost 9,655 input tokens.
- **Read only.** `--agent plan` is OpenCode's own read-only agent. It read a file and answered correctly.

**Events** (JSONL):
- `step_start`;
- `text` (`part.text`);
- `tool_use` (`part.tool`, `part.state.status`, `part.state.input`);
- `step_finish` (`part.reason`: `stop` or `tool-calls`; `part.tokens`: total, input, output, reasoning and cache; `part.cost`);
- `error` (`error.name`, `error.data.message`).

There is one `step_finish` per request to the model. The read task made 2 requests.

**Outcome.** Exit code 0 on success, and **exit code 1 with an `error` event** on failure: a model that does not exist gave `UnknownError`. Unlike Pi in JSON mode, the exit code is reliable. A `429` was not seen.

**Stopping.** `SIGINT` gives exit 130 and `SIGTERM` gives 143, within 0.05 s. No OpenCode process is left behind, and tokens are unknown.

**In WSL the program is found only from a login shell.** `~/.npm-global/bin` is on the PATH only through the login profile, so `wsl.exe -e bash script` did not find `opencode`. A plugin must start the agent through `bash -lc`, or use the program's full path.

**Dispatch row (OpenCode 1.19, read-only task):**
- program `opencode`;
- arguments `["run", "--pure", "--format", "json", "--agent", "plan", "--dir", "<folder>", "-m", "<provider>/<model>"]`;
- environment: the isolation variables above;
- `task_input: "stdin"`, closed after writing;
- output `jsonl`: done when the process exits; outcome from the exit code and the last `step_finish.reason` or `error`; tokens are the sum of `step_finish.tokens`; requests are the number of `step_finish`;
- stop with `SIGINT`.

A task that changes files uses the `build` agent in a new worktree.

## OpenCode 1.19.5, on Windows (verified live)

**Installation, and a packaged-app trap.**
- The first install, run from this Claude Code session, never reached the real system.
- The Claude desktop app is an MSIX package, and the commands it runs inherit its file-system virtualisation, outside Claude Code's own sandbox too. `npm install -g` wrote `%APPDATA%\npm` into `%LOCALAPPDATA%\Packages\<Claude package>\LocalCache\Roaming\npm`. The commands of this session could see that copy; the user's own PowerShell could not.
- The copy was removed. The maintainer then installed OpenCode from their own PowerShell and added the OpenRouter key with `opencode auth login` (`%USERPROFILE%\.local\share\opencode\auth.json`).
- **This matters beyond the spike.**
  - A coordinator running inside a packaged app may not see agents the user installed, and may install them where nobody else finds them.
  - Plugins must therefore look for agents in their real paths, and must never install anything.
  - AI Meter's own Store package behaves the same way. It already writes the MCP relay to `~/.aimeter` for that reason. M6 checks agent detection from the Store package.

**Start**, as a plugin would do it:
- the native program `%APPDATA%\npm\node_modules\opencode-ai\node_modules\opencode-windows-x64\bin\opencode.exe`, started with `UseShellExecute = false` and no shell;
- the task written to stdin, which is then closed;
- the isolation variables set in the process environment;
- the arguments `run --pure --format json --agent plan --dir <folder> -m <model>`.

**Results.**

| Check | Result |
| --- | --- |
| Read-only task | Done. Two `read` calls, then the answer, with three `step_finish` events, the same events as in WSL |
| A model that does not exist | Exit code 1 and an `error` event (`UnknownError`), the same as in WSL |
| Stopping | Windows has no `SIGINT`. `taskkill /PID <pid> /T /F` ended it in 0.75 s. OpenCode started no child processes, none was left, and tokens are unknown |

A gentler stop (`GenerateConsoleCtrlEvent`) was not tried.

**The PATH cannot be relied on.** Neither the Claude app's terminal nor a PowerShell window opened before the install found `opencode`. Plugins locate the program explicitly.

## Starting agents in WSL from Windows (verified live with AI Meter's descriptions)

`describe_dispatch` was built from these results, then checked by starting what it describes. The quoting followed Windows command-line rules, as Node's `spawn` applies them.

- **`wsl.exe --` is wrong.** It hands the command line to the default Linux shell, which re-reads it. A login-shell wrapper (`bash -lc 'exec "$0" "$@"' pi …`) reached bash as an empty program (`/bin/bash: : No such file or directory`).
- **`wsl.exe -e` runs a program without a shell.** The description therefore uses:
  ```
  wsl.exe -d <distro> --cd <folder> -e env PATH=<agent folder>:/usr/local/sbin:… [VARIABLE=value …] <full Linux path of the agent> <arguments>
  ```
  - The agent's full path, found by detection, makes a login shell unnecessary.
  - `env` sets the PATH, so the agent's interpreter (`/usr/bin/node` here) is found, along with the agent's variables.
  - Node version managers that keep `node` elsewhere are not covered.
- **Pi, a read task.**
  - The folder `C:\Users\…` became `/mnt/c/Users/…`.
  - The task contained `$HOME`, backquotes, quotes, `&`, `|` and `;`. It reached Pi unchanged, as Pi's own `message_start` event showed.
  - 3 requests, and the right answer.
- **OpenCode, a read task.** The JSON in `OPENCODE_CONFIG_CONTENT` survived `wsl.exe`.
  - `--dir` with the Windows folder failed inside WSL ("Failed to change directory"). Since `--cd` already sets the folder, the description passes `--dir .` in this case.
  - Then: right answer, about 10,400 tokens.
- **Stopping.** `taskkill /PID <wsl.exe> /T /F` also ended Pi inside WSL. Pi's process is named just `pi`. It was gone 3 s later.

## Consequences for M4 and M5

- `describe_dispatch` needs per agent:
  - `task_input` (`stdin` or `argument`);
  - how to tell the end (`agent_settled` for Pi);
  - where the outcome is (`stopReason`), not the exit code;
  - how to count requests and tokens;
  - how to stop.
- `release_capacity` takes tokens as unknown for cancelled tasks, and reports `rate_limited` when the agent's error message names a `429`.
- The second agent for the MVP is OpenCode, verified both in WSL and natively on Windows. Pi is verified in WSL only, since it is not installed on Windows here.
- The table needs per agent and per place:
  - **isolation:** environment variables, read-only mode, a free small model;
  - **how the program is found:** a login shell in WSL; the native `.exe` on Windows, never `.cmd` or `.ps1`.
- **Requirement from the maintainer (2026-11-02):** delegation must work for users who run their coordinator CLIs on Windows, without WSL. Every agent row therefore needs a verified Windows variant, not only a WSL one.
