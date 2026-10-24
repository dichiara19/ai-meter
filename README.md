# AI Meter

A native, lightweight Windows 11 widget that shows which AI sessions are running on your PC and how much of your usage limits is left: **Claude** (Claude Code, Cowork, desktop app), **ChatGPT / Codex**, **GitHub Copilot** and **Cursor**. Each service can be turned on or off in Settings; on first run AI Meter enables the ones it finds on your PC.

> Available in English, Italian, Spanish, French, German and Portuguese. It follows the Windows display language, or you can pick one in Settings. Translations live in `src/StringsTable.cs`: new languages are welcome.

**Download:** [latest release](https://github.com/dichiara19/ai-meter/releases/latest) · [Code signing policy](#code-signing-policy) · [Privacy policy](#privacy-policy)

## What you get

| Surface | What it does |
|---|---|
| **Notification area icon** | Click: full panel (Acrylic, like Windows' own flyouts). Right click: menu. Never shows up in the taskbar. |
| **Taskbar pill** | A compact pill inside the taskbar of the main monitor, placed in its free space (left corner or next to the notification area, based on how your taskbar is set up). It is a child window of the taskbar, so it hides with an auto-hiding taskbar and with full-screen apps. Click: full panel. |
| **Desktop widget** | Always stays *below* your windows, like part of the wallpaper. Compact cards; click a card header to show its sessions. Drag it by its header. |
| **Compact overlay** | A tiny always-on-top pill (off by default) with two micro buttons: collapse to a live ring icon, or close. Never steals focus, hides itself during full-screen games, videos and presentations, shows details on hover. |
| **Settings** | Built like Windows 11 Settings: connected services, what to show, where, start with Windows, theme, refresh rate, live resource usage. |

For each service: remaining share of the **5-hour** and **weekly** limits with reset times, plus the active sessions with title, model and status.

## Where the data comes from

Everything is read locally from files the official apps already write. Nothing is sent anywhere, with one exception you can turn off.

- **Claude**
  - Claude Code sessions (desktop app, CLI, also inside WSL, VS Code): `~/.claude/sessions`, titles and models from the Claude desktop app metadata.
  - Cowork tasks and whether the Claude app is open.
  - Limits **in real time** from your account's usage endpoint, using your existing Claude Code login. The token never leaves your PC except to call Anthropic, and it is refreshed by the Claude Code CLI itself: AI Meter never touches the refresh token. Without a login it falls back to the last value saved by the Claude app.
  The panel shows the 5-hour session with its start and end time, weekly and per-model weekly limits, cloud session credits, extra usage credits and how the week splits between Claude Code, chats and Cowork.
- **ChatGPT / Codex**: active Codex threads (app, CLI, VS Code) from Codex's local logs. Limits **in real time** by asking the installed `codex` program (`codex app-server`, JSON-RPC `account/rateLimits/read`): Codex uses its own login, AI Meter never reads or refreshes its tokens. Includes credits and free limit resets. If Codex is not available, the last value in its logs is used.
- **GitHub Copilot** (off by default): premium requests and monthly quotas from GitHub's Copilot usage API. You sign in to GitHub from AI Meter with a device code; the token is encrypted with Windows Data Protection (DPAPI) for your user only. GitHub shows the request as "Visual Studio Code", the official Copilot app that can read quotas.
- **Cursor**: included usage and on-demand spend for the billing cycle, from cursor.com, using the sign-in the Cursor app already keeps on your PC (read from its local database with the SQLite library built into Windows). AI Meter never refreshes or stores that token and never reads browser cookies. *Cursor support is new and has not been tested with a real account yet: reports are welcome.*
- Individual claude.ai / ChatGPT chats leave no trace on disk, so for those you only see whether the app is open.
- Service logos are read at runtime from the Claude and ChatGPT apps installed on your PC; they are trademarks of their owners and are not part of this repository.

## Resource usage

Measured on a Windows 11 laptop:

| | |
|---|---|
| RAM | ~100 MB as executable, ~145 MB with the PowerShell launcher |
| CPU at rest | ~0.02% |
| GPU | 0% (software drawing; Mica/Acrylic composed by Windows) |

How:
- WPF on .NET Framework 4.8, already part of Windows: no runtime to install, no browser engine.
- Software drawing: no Direct3D device on the GPU.
- No continuous animations: the window is redrawn only when something changes.
- Data collection runs on a low-priority thread with **EcoQoS** (efficiency cores). Refresh every 10 s, 30 s on battery or Energy Saver, 120 s when nothing is visible, and **stopped** while the PC is locked or asleep.
- Files are read only when they change, large logs only from their tail, and only the needed fields are cached.
- **WSL is never started**: it is read only if its VM is already running.
- The Claude limit is fetched over the network every 5 minutes (15 on battery), only while something is visible.
- Full-screen detection is event-driven, not polled.

## Download

From the [latest release](https://github.com/dichiara19/ai-meter/releases/latest):

- **AIMeter.zip**: unzip and run `AIMeter.exe`.
- **AIMeter-script.zip**: if Smart App Control blocks the unsigned executable, unzip and run `Start AI Meter.cmd`, which starts the same app from source with Windows PowerShell.

## Build and install

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Compiles with the `csc.exe` that ships with .NET Framework (no SDK needed), installs to `%LOCALAPPDATA%\Programs\AI Meter`, adds a Start menu shortcut. On first run it registers itself to start with Windows (you can turn this off in Settings). `-NoInstall` only builds into `bin\`.

Two install modes:

- **`-Mode script` (default).** Installs the source with the `AIMeter.ps1` launcher. Windows PowerShell, which is signed by Microsoft, compiles the C# code in memory at startup (about 2 s) and runs it, without a console window (`conhost --headless`). This works with **Smart App Control on**, at the cost of roughly 45 MB more RAM.
- **`-Mode exe`.** Installs the compiled `AIMeter.exe`. Lighter, but with Smart App Control on Windows blocks it until releases are signed (see [Code signing policy](#code-signing-policy)).

Command line: `--settings` opens Settings of the running instance, `--gpu` draws with the GPU.

## Uninstall

Settings → turn off *Start with Windows*, choose *Exit* from the menu, then delete `%LOCALAPPDATA%\Programs\AI Meter`, `%APPDATA%\AIMeter` and the *AI Meter* Start menu shortcut.

## Project layout

```
src/Native.cs          Win32: processes (Toolhelp), DWM, EcoQoS, power state, full-screen detection
src/Probe.cs           shared probe (processes, WSL, file cache) and the collector that runs each service
src/ProviderClaude.cs  Claude: Claude Code and Cowork sessions, usage API
src/ProviderCodex.cs   ChatGPT / Codex: threads from local logs, limits via codex app-server
src/ProviderCopilot.cs GitHub Copilot: device-code sign-in, quotas API
src/Model.cs           data model and JSON helpers
src/Settings.cs        preferences and start with Windows
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

It reads local files written by the Claude and ChatGPT/Codex apps and keeps its own settings in `%APPDATA%\AIMeter`, on your PC only.

Network connections are limited to reading your own usage:

- **Claude limits** (on by default): a request to Anthropic's API (`api.anthropic.com`) with the OAuth access token of your existing Claude Code login. Governed by [Anthropic's privacy policy](https://www.anthropic.com/legal/privacy). You are told about it on first launch and can turn it off in Settings → *Limiti in tempo reale*.
- **Codex limits**: requested by the installed `codex` program with its own login; AI Meter never handles that token.
- **GitHub Copilot quotas** (off by default): a request to `api.github.com`, only if you enable Copilot and sign in to GitHub.
- **Cursor usage**: a request to `cursor.com` with the Cursor app's own sign-in, only if Cursor is enabled.

With these off, AI Meter makes no network connections at all.


## License

MIT. Not affiliated with Anthropic or OpenAI. Claude is a trademark of Anthropic; ChatGPT and Codex are trademarks of OpenAI.
