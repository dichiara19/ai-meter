<div align="center">

<img src="assets/logo.png" alt="" width="96">

# AI Meter

**AI limits, local models and coding agents, quietly in the Windows 11 taskbar.**

[![Latest release](https://img.shields.io/github/v/release/dichiara19/ai-meter?label=download&color=2563eb)](https://github.com/dichiara19/ai-meter/releases/latest)
[![Windows 11](https://img.shields.io/badge/Windows-11-0078d4)](#install)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-16a34a)](LICENSE)
[![Languages](https://img.shields.io/badge/languages-6-7c3aed)](#languages)

</div>

<p align="center"><img src="docs/media/hero-taskbar.png" alt="The AI Meter pill inside the Windows 11 taskbar, with its tooltip: Claude, ChatGPT and Copilot limits and a local model loaded in Ollama"></p>

That's all of it, most of the time: a pill **inside** the taskbar. No widget to arrange, no window stuck on top of your work. Hover for the summary, click for everything.

- **Lives in the taskbar.** The pill is part of the taskbar, not a window floating on it: it hides with an auto-hiding taskbar and with full-screen apps, follows the taskbar's light or dark mode and finds the free space for your layout. A desktop widget and a compact overlay are there if you want them.
- **Looks like Windows made it.** Fluent design, Mica and Acrylic, your accent color, Windows fonts and notifications, a Settings page built like Windows 11 Settings. Six languages.
- **Practically free to run.** About 0.02% CPU and 120 MB at rest. No Electron, no WebView, no runtime to install: WPF on the .NET Framework already in Windows. Efficiency cores, slower refresh on battery and when nothing is visible, nothing at all while the PC is locked.
- **Windows and WSL, one view.** Claude Code, Codex and other coding agents running in WSL, model servers inside WSL (NAT or mirrored networking), the video memory of the WSL VM: shown next to their Windows counterparts. WSL is never started, and nothing is launched inside it.
- **Local models are first-class.** Ollama, LM Studio, llama.cpp, llama-swap, vLLM and more: video memory, context window, how much context the current request uses, requests in flight, and which coding agent (OpenCode, Pi, Gemini CLI, Qwen Code…) is talking to which model.
- **Your subscriptions, before the wall.** Claude, ChatGPT / Codex, GitHub Copilot, Cursor, OpenRouter, DeepSeek, Kimi: 5-hour and weekly limits, a forecast ("at this pace it runs out at 15:40"), a notification at 20% and 5% that tells you which service still has room, and a tip before a reset when part of a limit would go unused. Pick which limit each service shows in the pill, and get notified whenever the limits you choose reset.
- **Private and open.** No telemetry; the only network requests are each service's own usage endpoint. Apache 2.0 licensed, and a new service is a [20-line JSON file](docs/PROVIDERS.md).

<p align="center"><img src="docs/media/demo.gif" alt="A notification warns that the Claude session is running low, then a click on the pill opens the panel with every service, local models and agents" width="440"></p>

<table>
<tr>
<td width="50%"><img src="docs/media/panel-dark.png" alt="The panel in dark mode"></td>
<td width="50%"><img src="docs/media/panel-light.png" alt="The panel in light mode"></td>
</tr>
<tr>
<td colspan="2" align="center"><img src="docs/media/notification.png" alt="A Windows notification: Claude session 18% left, runs out at this pace before it resets, ChatGPT still has 77%" width="60%"></td>
</tr>
</table>

## Install

1. Download **AIMeter-script.zip** from the [latest release](https://github.com/dichiara19/ai-meter/releases/latest) and unzip it anywhere.
2. Run **`Start AI Meter.cmd`**. It works with Smart App Control on: Windows PowerShell, signed by Microsoft, compiles and runs the source you can read.
3. The pill appears in your taskbar: AI Meter finds the services on your PC and starts with Windows. In Settings you can add a desktop widget or a compact overlay.

Prefer a single executable? **AIMeter.zip** has `AIMeter.exe` (unsigned for now, see [Code signing policy](#code-signing-policy)). A Microsoft Store version is on the way.

<a id="languages"></a>Available in English, Italian, Spanish, French, German and Portuguese. It follows the Windows display language, or you can pick one in Settings. Translations live in `src/StringsTable.cs`: new languages are welcome.

## What you get

| Surface | What it does |
|---|---|
| **Taskbar pill** (default) | A compact pill inside the taskbar of the main monitor, placed in its free space (left corner or next to the notification area, based on how your taskbar is set up). It is a child window of the taskbar, so it hides with an auto-hiding taskbar and with full-screen apps. Local models get their own area at the end: video memory in use, with a dot while a model is working. Hover: summary. Click: full panel. |
| **Notification area icon** | Click: full panel (Acrylic, like Windows' own flyouts). Right click: menu. Never shows up in the taskbar. |
| **Desktop widget** | Off by default. Always stays *below* your windows, like part of the wallpaper. Compact cards; click a card header to show its sessions. Drag it by its header. |
| **Compact overlay** | A tiny always-on-top pill (off by default) with two micro buttons: collapse to a live ring icon, or close. Never steals focus, hides itself during full-screen games, videos and presentations, shows details on hover. |
| **Settings** | Built like Windows 11 Settings: connected services, what to show, where, start with Windows, theme, refresh rate, live resource usage. |

For each service: remaining share of the **5-hour** and **weekly** limits with reset times, plus the active sessions with title, model and status.

**Forecast and notifications.** At the current pace, AI Meter tells you when a limit will run out if that happens before it resets ("At this pace it runs out at 15:40"), in the panel, in tooltips and with a yellow bar in the pill. The pace is the trend of the last hour and a half of readings, or the average since the window started. Windows notifications arrive when a limit drops below 20% and 5%, with the forecast and the other service that still has room ("ChatGPT still has 80% left"), and when a limit you had almost used up is available again. Each one comes once per window, follows Do not disturb, and can be turned off in Settings.

## Where the data comes from

Everything is read locally from files the official apps already write. Nothing is sent anywhere, with one exception you can turn off.

- **Claude**
  - Claude Code sessions (desktop app, CLI, also inside WSL, VS Code): `~/.claude/sessions`, titles and models from the Claude desktop app metadata.
  - Cowork tasks and whether the Claude app is open.
  - Limits **in real time** from your account's usage endpoint, using your existing Claude Code login. The token never leaves your PC except to call Anthropic, and it is refreshed by the Claude Code CLI itself: AI Meter never touches the refresh token. Without a login it falls back to the last value saved by the Claude app.
  The panel shows the 5-hour session with its start and end time, weekly and per-model weekly limits, cloud session credits, extra usage credits and how the week splits between Claude Code, chats and Cowork.
- **ChatGPT / Codex**: active Codex threads (app, CLI, VS Code) from Codex's local logs. Limits **in real time** by asking the installed `codex` program (`codex app-server`, JSON-RPC `account/rateLimits/read`): Codex uses its own login, AI Meter never reads or refreshes its tokens. Includes credits and free limit resets. If Codex is not available, the last value in its logs is used.
- **GitHub Copilot** (off by default): premium requests and monthly quotas from GitHub's Copilot usage API. You sign in to GitHub from AI Meter with a device code; the token is encrypted with Windows Data Protection (DPAPI) for your user only. GitHub shows the request as "Visual Studio Code", the official Copilot app that can read quotas.
- **Services described by a definition file** ([`providers/`](providers), format in [docs/PROVIDERS.md](docs/PROVIDERS.md)):
  - **Cursor**: included usage and on-demand spend for the billing cycle, from cursor.com, using the sign-in the Cursor app already keeps on your PC (read from its local database with the SQLite library built into Windows). AI Meter never refreshes or stores that token and never reads browser cookies.
  - **OpenRouter**: spending and limit of your API key. **DeepSeek** and **Kimi (Moonshot)**: account balance. You paste an API key in Settings (or set the usual environment variable); it is encrypted with Windows Data Protection.
  - *These have been tested against recorded API responses, not yet with real accounts: reports are welcome.*
- **Local models**: AI Meter looks for model servers listening on this PC and recognises them from their own API: Ollama, LM Studio, llama.cpp `llama-server` (also in router mode), llama-swap, vLLM, KoboldCpp, LocalAI and text-generation-webui. For each loaded model it shows video and system memory, the context window, requests in progress and how much context the current request uses, when the server tells. Servers inside WSL are found only while WSL is already running (`ss` inside the distribution, at most once a minute); video memory comes from the Windows GPU counters (in WSL it is all charged to the WSL virtual machine). Only `127.0.0.1` is contacted, and only with read requests that never load a model.
- **Coding agents**: sessions active in the last 30 minutes of OpenCode, Kilo, Pi, Gemini CLI, Qwen Code, Goose, Crush and Cline CLI, read from their local session files and databases (Windows and WSL), with title, model, context in use and whether the agent is working. A session that uses a model loaded locally is shown under that model.
- Individual claude.ai / ChatGPT chats leave no trace on disk, so for those you only see whether the app is open.
- Service logos are read at runtime from the apps installed on your PC; they are trademarks of their owners and are not part of this repository.

## Resource usage

Measured on a Windows 11 laptop:

| | |
|---|---|
| RAM | ~100 MB as executable, ~125 MB private with the PowerShell launcher |
| CPU at rest | ~0.016% of the machine (0.26% of one core) with the taskbar pill, Claude, Codex and a local model server in WSL |
| GPU | 0% (software drawing; Mica/Acrylic composed by Windows) |

How:
- WPF on .NET Framework 4.8, already part of Windows: no runtime to install, no browser engine.
- Software drawing: no Direct3D device on the GPU.
- No continuous animations: the window is redrawn only when something changes.
- Data collection runs on a low-priority thread with **EcoQoS** (efficiency cores). Refresh every 10 s while the panel or Settings are open, every 30 s when only the pill, widget or overlay are visible, twice as slow on battery or Energy Saver, 120 s when nothing is visible, and **stopped** while the PC is locked or asleep. A refresh takes about 50 ms.
- The taskbar pill checks every 2 s, with two window-position reads, whether the taskbar moved; it repositions itself only when something changed.
- Files are read only when they change, large logs only from their tail, and only the needed fields are cached.
- **WSL is never started**: it is read only if its VM is already running, and no process is ever launched inside it (listening ports are read from the kernel's TCP table as a file). AI Meter asks Windows which distributions are running at most once a minute.
- Local model servers are contacted only on `127.0.0.1`; a port that does not answer HTTP is dropped after one short request. GPU memory is read with PDH only while a local server runs.
- The Claude limit is fetched over the network every 5 minutes (15 on battery), only while something is visible.
- Full-screen detection is event-driven, not polled.

## Download files

From the [latest release](https://github.com/dichiara19/ai-meter/releases/latest):

- **AIMeter.zip**: unzip and run `AIMeter.exe`.
- **AIMeter-script.zip**: if Smart App Control blocks the unsigned executable, unzip and run `Start AI Meter.cmd`, which starts the same app from source with Windows PowerShell.

A Microsoft Store version, signed by Microsoft and updated automatically, is in preparation: see [docs/STORE.md](docs/STORE.md).

## Build and install

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Compiles with the `csc.exe` that ships with .NET Framework (no SDK needed), installs to `%LOCALAPPDATA%\Programs\AI Meter`, adds a Start menu shortcut. On first run it registers itself to start with Windows (you can turn this off in Settings). `-NoInstall` only builds into `bin\`.

Two install modes:

- **`-Mode script` (default).** Installs the source with the `AIMeter.ps1` launcher. Windows PowerShell, which is signed by Microsoft, compiles the C# code in memory at startup (about 2 s) and runs it, without a console window (`conhost --headless`). This works with **Smart App Control on**, at the cost of roughly 45 MB more RAM.
- **`-Mode exe`.** Installs the compiled `AIMeter.exe`. Lighter, but with Smart App Control on Windows blocks it until releases are signed (see [Code signing policy](#code-signing-policy)).

`-Package` also builds the MSIX package for the Microsoft Store (`bin\AIMeter.msix`, needs the Windows SDK; see [docs/STORE.md](docs/STORE.md)).

Command line: `--settings` opens Settings of the running instance and `--gpu` draws with the GPU.

## Uninstall

Settings → turn off *Start with Windows*, choose *Exit* from the menu, then delete `%LOCALAPPDATA%\Programs\AI Meter`, `%APPDATA%\AIMeter`, the *AI Meter* shortcuts in the Start menu and in the Startup folder, and the registry key `HKCU\Software\Classes\AppUserModelId\AIMeter.App` (name and icon of its notifications).

## Project layout

```
src/Native.cs          Win32: processes (Toolhelp), DWM, EcoQoS, power state, full-screen detection
src/Probe.cs           shared probe (processes, WSL, file cache) and the collector that runs each service
src/ProviderClaude.cs  Claude: Claude Code and Cowork sessions, usage API
src/ProviderCodex.cs   ChatGPT / Codex: threads from local logs, limits via codex app-server
src/ProviderCopilot.cs GitHub Copilot: device-code sign-in, quotas API
src/Model.cs           data model and JSON helpers
src/ProviderDefined.cs services described by JSON definitions, API keys, SQLite reader
src/ProviderLocal.cs   local model servers: discovery on Windows and WSL, loaded models, memory, context
src/CodingAgents.cs    sessions of terminal coding agents (OpenCode, Pi, Gemini CLI, Qwen Code, …)
src/GpuMemory.cs       video memory per process from the Windows GPU counters
src/Forecast.cs        when each limit runs out at the current pace; low-limit and reset notifications, tips
providers/*.json       built-in service definitions
tools/render-media.ps1 regenerates the README images (docs/media) from the real UI with demo data
src/Settings.cs        preferences and start with Windows
src/PackageStartup.cs  start with Windows in the Microsoft Store package (StartupTask)
src/Theme.cs           Fluent light/dark tokens, Windows accent color, control styles
src/Ui.cs              cards, bars, session rows, ring icon, logos, Mica windows
src/Surfaces.cs        desktop widget, overlay pill, panel
src/SettingsWindow.cs  Settings
src/App.cs             startup, notification icon, menu, system events, refresh loop
src/Strings.cs         translation engine (English text as key, Windows language or user choice)
src/StringsTable.cs    translations: Italian, Spanish, French, German, Portuguese
assets/app.ico         app icon (generated by build.ps1)
```

## Code signing policy

> Current releases are **not signed**. The project plans to apply for free open source code signing once it has broader adoption; this policy describes how signed releases will be produced.

- Releases are built only by the [GitHub Actions workflow](.github/workflows/build.yml) of this repository, from the tagged source. No binary is built or uploaded by hand.
- Every signing request is approved manually.
- Committers and reviewers: [dichiara19](https://github.com/dichiara19)
- Approvers: [dichiara19](https://github.com/dichiara19)
- Contributions from people outside the team (pull requests) are reviewed by a team member before merge, with particular attention to build scripts and CI configuration.

## Privacy policy

AI Meter does not collect, store or send any data to its authors, and contains no telemetry, analytics or update checks.

It reads local files written by the AI apps and coding agents on your PC and keeps its own settings in `%APPDATA%\AIMeter`, on your PC only.

Network connections are limited to reading your own usage:

- **Claude limits** (on by default): a request to Anthropic's API (`api.anthropic.com`) with the OAuth access token of your existing Claude Code login. Governed by [Anthropic's privacy policy](https://www.anthropic.com/legal/privacy). You are told about it on first launch and can turn it off in Settings → *Limiti in tempo reale*.
- **Codex limits**: requested by the installed `codex` program with its own login; AI Meter never handles that token.
- **GitHub Copilot quotas** (off by default): a request to `api.github.com`, only if you enable Copilot and sign in to GitHub.
- **Cursor usage**: a request to `cursor.com` with the Cursor app's own sign-in, only if Cursor is enabled.
- **OpenRouter, DeepSeek, Kimi**: a request to `openrouter.ai`, `api.deepseek.com` or `api.moonshot.ai` with your API key, only if you turn the service on and add a key.
- **Definitions you add**: a request to the address named in the file.
- **Local models**: requests to `127.0.0.1` only, which never leave the PC.

With these off, AI Meter makes no network connections at all.


## Contributing

Bug reports, translations, new services and code are welcome: see [CONTRIBUTING.md](CONTRIBUTING.md). Questions and ideas go to [Discussions](https://github.com/dichiara19/ai-meter/discussions), security issues to a [private report](SECURITY.md). Everyone is expected to follow the [code of conduct](CODE_OF_CONDUCT.md).

## Third-party services

AI Meter is an independent project, not affiliated with, endorsed or sponsored by Anthropic, OpenAI, GitHub, Microsoft, Anysphere (Cursor) or any other service it shows. Names and logos belong to their owners (see [NOTICE](NOTICE)); logos are read at runtime from apps installed on your PC, and screenshots show them only to identify the services.

Some of the usage data comes from endpoints that the services use in their own apps but do not document publicly. They can change or stop working at any time, and AI Meter reads them only for your own account, with the sign-in those apps already have. You are responsible for using each service according to its terms.

## License

Copyright 2026 Giuseppe Gabriele Di Chiara. Licensed under the [Apache License 2.0](LICENSE); versions up to 0.3.1 were released under the MIT License.

The name "AI Meter" and its logo are trademarks and are not covered by the license: forks and modified builds need their own name and icon, see [TRADEMARKS.md](TRADEMARKS.md).
