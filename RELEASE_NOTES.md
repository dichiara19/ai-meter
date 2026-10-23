## What's new in 0.2.0

**New ways to see it**
- **Taskbar pill.** AI Meter reads how your taskbar is set up (position, icon alignment, Widgets button, auto-hide) and places a compact pill in its free space: the left corner when it is free, otherwise next to the notification area. A click opens the full panel, sliding in from the side.
- **Collapsible overlay.** The always-on-top pill has two small buttons: minimize it to a tiny icon with live rings, or close it. No more empty space below it.

**Live data**
- **Codex limits in real time**, asked to the installed `codex` program with its own login. AI Meter never reads Codex tokens. It also shows Codex credits and free limit resets.
- **Claude:** the 5-hour session now shows its start and end time. New: per-model weekly limits, cloud session credits, extra usage credits, and how the week splits between Claude Code, chats and Cowork.
- **GitHub Copilot** (off by default): premium requests and monthly quotas. Sign in to GitHub from AI Meter with a device code; the token is encrypted with Windows Data Protection.

**Everywhere**
- **Six languages:** English, Italian, Spanish, French, German and Portuguese. AI Meter follows the Windows language, or you can pick one in Settings; changing it applies right away.
- **Services you choose.** Settings has a switch for each service; on first run AI Meter turns on the ones it finds on your PC.
- Cleaner panel footer; a service's card tells you when its limits are not live.

## Download

Two packages, same app:

- **AIMeter.zip**: the executable. Unzip and run `AIMeter.exe`.
- **AIMeter-script.zip**: for PCs with **Smart App Control** on, which blocks unsigned executables. Unzip and run `Start AI Meter.cmd`: Windows PowerShell compiles and starts the app from source. Releases are not signed yet.

Requires Windows 10 or 11 (.NET Framework 4.8, already part of Windows).

Built by GitHub Actions from the tagged source of this repository.
[Code signing policy](https://github.com/dichiara19/ai-meter#code-signing-policy) · [Privacy policy](https://github.com/dichiara19/ai-meter#privacy-policy)
