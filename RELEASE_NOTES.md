<p align="center"><img src="https://raw.githubusercontent.com/dichiara19/ai-meter/v0.3.0/docs/media/demo.gif" alt="AI Meter in the taskbar: a notification warns that the Claude session is running low, then the panel opens" width="440"></p>

## What's new in 0.3.0

**It warns you before you hit the wall**
- **Forecast.** When a limit would run out before it resets, AI Meter tells you when: "At this pace it runs out at 15:40", under the bar, in tooltips, and with a yellow bar in the pill. The pace is the trend of the last hour and a half, or the average since the window started.
- **Windows notifications** when a limit drops below 20% and 5%, with the forecast and the other service that still has room ("ChatGPT still has 77% left"), and when a limit you had almost used up is available again. Once per window, they follow Do not disturb, and you can turn them off in Settings → Notifications.

**Local models**
- AI Meter finds the model servers running on your PC, on Windows or inside WSL (NAT and mirrored networking), and recognises **Ollama, LM Studio, llama.cpp** (also router mode), **llama-swap, vLLM, KoboldCpp, LocalAI** and **text-generation-webui**.
- For each loaded model: video and system memory, context window, requests in progress and how much context the current request uses.
- Its own area in the taskbar pill: video memory in use, with a dot while a model is working.
- Only `127.0.0.1` is contacted, with read requests that never load a model. WSL is never started and no process is launched inside it.

**Coding agents**
- Open sessions of **OpenCode, Pi, Gemini CLI, Qwen Code, Goose, Crush, Cline** and **Kilo** (on Windows and in WSL), with title, model, context in use and whether the agent is working. A session that uses a local model appears under that model.

**More services, and you can add your own**
- **Cursor**: included usage and on-demand spend, with the Cursor app's own sign-in.
- **OpenRouter** (key spending and limit), **DeepSeek** and **Kimi (Moonshot)** (account balance): paste an API key in Settings, it is encrypted with Windows Data Protection.
- These services are described by small JSON files: anyone can add one without code ([docs/PROVIDERS.md](https://github.com/dichiara19/ai-meter/blob/main/docs/PROVIDERS.md)). Drop a file in `%APPDATA%\AIMeter\providers`, or send a pull request.
- *Cursor, OpenRouter, DeepSeek and Kimi were tested against recorded API responses, not real accounts yet: reports are welcome.*

**More native**
- **The taskbar pill now lives inside the taskbar** (a child window of it), like taskbar widgets should on Windows 11: it hides with an auto-hiding taskbar and full-screen apps, and a click on the taskbar no longer covers it.
- **New icon**, and AI Meter's own name and icon on the taskbar button of its windows and on its notifications (instead of Windows PowerShell).

**Lighter**
- About 0.016% of the PC's CPU at rest, measured with the taskbar pill, Claude, Codex and a local model server in WSL, and fewer wake-ups: data refreshes every 30 s when only the pill or the widget are visible, every 10 s with the panel open, twice as slow on battery.
- Video memory is read with PDH only while a local model server runs; the pill repositions itself only when the taskbar changes.

**For contributors**
- `--demo` shows demo data; `tools/render-media.ps1` regenerates the README images from the real interface.
- A Microsoft Store package (MSIX, signed by the Store) is built by CI and on its way to the Store.

## Download

Two packages, same app:

- **AIMeter-script.zip** (recommended): unzip and run `Start AI Meter.cmd`. It works with **Smart App Control** on: Windows PowerShell, signed by Microsoft, compiles and starts the app from source.
- **AIMeter.zip**: the executable. Unzip and run `AIMeter.exe`. Releases are not signed yet, so Smart App Control blocks it.

Requires Windows 10 or 11 (.NET Framework 4.8, already part of Windows). Upgrading from 0.2.0: exit AI Meter from its menu, replace the files, start it again; your settings are kept.

Built by GitHub Actions from the tagged source of this repository.
[Code signing policy](https://github.com/dichiara19/ai-meter#code-signing-policy) · [Privacy policy](https://github.com/dichiara19/ai-meter#privacy-policy)
