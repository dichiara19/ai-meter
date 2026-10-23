# AI Meter

A native, lightweight Windows 11 widget that shows which AI sessions are running on your PC and how much of your usage limits is left: **Claude** (Claude Code, Cowork, desktop app) and **ChatGPT / Codex**.

> The interface is currently in Italian. Translations are welcome.

**Download:** [latest release](https://github.com/dichiara19/ai-meter/releases/latest) · [Code signing policy](#code-signing-policy) · [Privacy policy](#privacy-policy)

## What you get

| Surface | What it does |
|---|---|
| **Notification area icon** | Click: full panel (Acrylic, like Windows' own flyouts). Right click: menu. Never shows up in the taskbar. |
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
- **ChatGPT / Codex**: active Codex threads (app, CLI, VS Code) and ChatGPT plan limits from the `rate_limits` events in Codex's local logs. No network access.
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
src/Collector.cs       data collection with caching and efficiency rules
src/Model.cs           data model and JSON helpers
src/Settings.cs        preferences and start with Windows
src/Theme.cs           Fluent light/dark tokens, Windows accent color, control styles
src/Ui.cs              cards, bars, session rows, ring icon, logos, Mica windows
src/Surfaces.cs        desktop widget, overlay pill, panel
src/SettingsWindow.cs  Settings
src/App.cs             startup, notification icon, menu, system events, refresh loop
assets/app.ico         app icon (generated by build.ps1)
```

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

> Current releases are **not signed**. The project will reapply to SignPath Foundation once it has broader adoption; this policy describes how signed releases will be produced.

- Releases are built only by the [GitHub Actions workflow](.github/workflows/build.yml) of this repository, from the tagged source. No binary is built or uploaded by hand.
- Every signing request is approved manually.
- Committers and reviewers: [dichiara19](https://github.com/dichiara19)
- Approvers: [dichiara19](https://github.com/dichiara19)
- Contributions from people outside the team (pull requests) are reviewed by a team member before merge, with particular attention to build scripts and CI configuration.

## Privacy policy

AI Meter does not collect, store or send any data to its authors, and contains no telemetry, analytics or update checks.

It reads local files written by the Claude and ChatGPT/Codex apps and keeps its own settings in `%APPDATA%\AIMeter`, on your PC only.

The single network connection it makes is optional and on by default: **real-time Claude limits**. When enabled, AI Meter sends a request to Anthropic's API (`api.anthropic.com`) using the OAuth access token of your existing Claude Code login, to read your own usage percentages. That request is governed by [Anthropic's privacy policy](https://www.anthropic.com/legal/privacy). You are told about it on first launch, and you can turn it off in Settings → *Limiti in tempo reale*; with it off, AI Meter makes no network connections at all.

## License

MIT. Not affiliated with Anthropic or OpenAI. Claude is a trademark of Anthropic; ChatGPT and Codex are trademarks of OpenAI.
