<p align="center"><img src="https://raw.githubusercontent.com/dichiara19/ai-meter/v0.5.0/docs/media/hero-taskbar.png" alt="The AI Meter pill inside the Windows 11 taskbar, with its tooltip" width="720"></p>

## What's new in 0.5.0

- **Google Antigravity.** The 5-hour and weekly quotas of your Google AI plan in Antigravity, per model group (Gemini, Claude + GPT), in the pill and in the panel. AI Meter reads the sign-in of Antigravity CLI, on Windows or in WSL, and lets the CLI renew it. Turn it on in Settings → Services. Limit choice, reset notifications and tips work here too.

The Gemini app and Gemini CLI for personal accounts do not expose their limits, so they are not shown.

## Download

Two packages, same app:

- **AIMeter-script.zip** (recommended): unzip and run `Start AI Meter.cmd`. It works with **Smart App Control** on: Windows PowerShell, signed by Microsoft, compiles and starts the app from source.
- **AIMeter.zip**: the executable. Unzip and run `AIMeter.exe`. Releases are not signed yet, so Smart App Control blocks it.

Requires Windows 10 or 11 (.NET Framework 4.8, already part of Windows). Upgrading: exit AI Meter from its menu, replace the files, start it again; your settings are kept.

Built by GitHub Actions from the tagged source of this repository.
[Code signing policy](https://github.com/dichiara19/ai-meter#code-signing-policy) · [Privacy policy](https://github.com/dichiara19/ai-meter#privacy-policy)
