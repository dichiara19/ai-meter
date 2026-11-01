## What changed in 0.8.5

- **Running Pi sessions found anywhere.** AI Meter reads the session folder of Pi processes that are running now, also when Pi is started by a script with `--session-dir`, `PI_CODING_AGENT_DIR` or `PI_CODING_AGENT_SESSION_DIR`, or inside a `bwrap` sandbox. Windows processes are read from their command line; WSL processes from `\\wsl.localhost`, only while the distribution is already running. Today's model costs include these sessions.
- **Sessions on their service's card.** A Pi session that uses OpenRouter, Experiential Labs, Claude, ChatGPT, Copilot or Antigravity is shown on that service's card. Sessions with providers that have no card stay under Local models → Coding agents.
- **Session lists fold in the taskbar window too.** Before, only the desktop widget could fold them. The Local models card now folds its agent sessions as well, and the choice is shared between the two windows.
- **Free models you can use.** Click the free-model count to open the full list, with each model's end date or context window. Click a model to copy its ID for Pi, OpenCode or your scripts.

## Download

- **AIMeter-script.zip** (recommended): unzip and run `Start AI Meter.cmd`. Works with Smart App Control enabled.
- **AIMeter.zip**: unzip and run `AIMeter.exe`. The executable is unsigned and may be blocked by Smart App Control.

To update, exit AI Meter, replace the files in its existing folder and start it again. Settings and usage history are retained. Requires Windows 10 or 11 with .NET Framework 4.8.
