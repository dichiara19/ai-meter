<p align="center"><img src="https://raw.githubusercontent.com/dichiara19/ai-meter/v0.3.2/docs/media/hero-taskbar.png" alt="The AI Meter pill inside the Windows 11 taskbar, with its tooltip" width="720"></p>

## What's new in 0.3.2

- **Reliable start with Windows.** AI Meter now starts at sign-in from an "AI Meter" shortcut in the Startup folder. The previous registry entry could be skipped by Windows and showed up in Settings → Apps → Startup as "Console Window Host". Existing installs move over by themselves on the next launch, and turning it off in Windows Settings is respected.
- **Right-click menu with more than one monitor.** The menu opens at the right size and position when your monitors use different scaling.
- **Apache License 2.0.** The downloads now include LICENSE and NOTICE. AI Meter is licensed under Apache 2.0 from this release; the name and logo are covered by [TRADEMARKS.md](https://github.com/dichiara19/ai-meter/blob/v0.3.2/TRADEMARKS.md).

## Download

Two packages, same app:

- **AIMeter-script.zip** (recommended): unzip and run `Start AI Meter.cmd`. It works with **Smart App Control** on: Windows PowerShell, signed by Microsoft, compiles and starts the app from source.
- **AIMeter.zip**: the executable. Unzip and run `AIMeter.exe`. Releases are not signed yet, so Smart App Control blocks it.

Requires Windows 10 or 11 (.NET Framework 4.8, already part of Windows). Upgrading: exit AI Meter from its menu, replace the files, start it again; your settings are kept.

Built by GitHub Actions from the tagged source of this repository.
[Code signing policy](https://github.com/dichiara19/ai-meter#code-signing-policy) · [Privacy policy](https://github.com/dichiara19/ai-meter#privacy-policy)
