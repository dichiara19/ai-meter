<p align="center"><img src="https://github.com/dichiara19/ai-meter/releases/download/v0.8.0/history-dark.png" alt="The AI Meter usage history dashboard, composed from the real interface with sample data" width="1080"></p>

## What's new in 0.8.0

**A local history of what you use.** Open **Usage history** from the notification-area menu or **Settings → Usage history** to explore consumption over time. Collection starts when you first run this version and continues with the panel closed.

- **Daily activity and session measurements.** Charts and model/tool summaries separate responses, input, output, cache reads, cache writes and tool-computed API costs. Supported session records come from Claude Code, Codex, Pi, OpenCode and Kilo on Windows and already-running WSL. Repeated snapshots, streaming updates and inherited Pi replies are deduplicated.
- **Quota cycles and consumption signals.** Readings preserve account scope, original timestamps, resets, units and data quality. Cycles show observed peaks and the quota remaining at the last reading. Signals compare active consumption rates only when enough comparable history exists; they cannot prove that a provider changed its billing rules.
- **Optional feedback on value for consumption.** Rate whether the useful result was worth the quota or money consumed: yes, partly or no. Feedback requests are off by default and can be enabled in Settings. You can also rate manually, edit or clear a rating. Ignoring a request leaves the session unrated.
- **A dashboard that explains its data.** Settings-style surfaces, daily and quota charts, light/dark themes, responsive session details, keyboard focus and translated empty states. New history, a first reading, missing counters and reported zeroes are presented differently. The dashboard does not report an absence of anomalies before a comparison baseline exists.
- **Local storage under your control.** Collection can be disabled without deleting saved data. View archive size, export retained measurements and feedback as JSONL, or delete the history. The archive contains no prompts, response text, source code, session titles or credentials. There is no telemetry.
- **Windows reliability and coverage.** Lock and suspend states remain separate, so resuming the PC does not collect through a locked session. Local-calendar-day summaries handle timezone boundaries, including skipped and repeated midnights. The regression suite now contains 55 isolated tests.

Screenshots use sample data. History cannot reconstruct unobserved activity, and coverage limits are recorded explicitly. Tool-computed API costs are not subscription charges; account-wide quota is not attributed to individual sessions. Some counters may be unavailable from the source.

[Light theme screenshot](https://github.com/dichiara19/ai-meter/releases/download/v0.8.0/history-light.png) · [History guide and collection limits](https://github.com/dichiara19/ai-meter/blob/v0.8.0/docs/HISTORY.md) · [Previous release](https://github.com/dichiara19/ai-meter/releases/tag/v0.7.2)

## Download

Two packages, same app:

- **AIMeter-script.zip** (recommended): unzip and run `Start AI Meter.cmd`. It works with **Smart App Control** on: Windows PowerShell, signed by Microsoft, compiles and starts the app from source.
- **AIMeter.zip**: the executable. Unzip and run `AIMeter.exe`. Releases are not signed yet, so Smart App Control blocks it.

Requires Windows 10 or 11 (.NET Framework 4.8, already part of Windows). Upgrading: exit AI Meter from its menu, replace the files, start it again; your settings are kept. This update creates a new local history archive; earlier usage is not backfilled.

Built by GitHub Actions from the tagged source of this repository.
[Code signing policy](https://github.com/dichiara19/ai-meter#code-signing-policy) · [Privacy policy](https://github.com/dichiara19/ai-meter#privacy-policy)
