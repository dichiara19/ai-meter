<p align="center"><img src="https://raw.githubusercontent.com/dichiara19/ai-meter/v0.7.1/docs/media/hero-taskbar.png" alt="The AI Meter pill inside the Windows 11 taskbar, with its tooltip" width="720"></p>

## What's new in 0.7.1

- **No more "AI Meter could not start" at sign-in.** When Windows refused the taskbar pill while the taskbar was being recreated (at sign-in, on resume or when Explorer restarts), AI Meter closed with "Access is denied". It now tries again two seconds later, and any unexpected error is written to `errors.log` instead of closing the app.

Everything new in 0.7 (API keys found from your agents, budgets and spend per period, today's models, OpenRouter account credits) is in the [0.7.0 notes](https://github.com/dichiara19/ai-meter/releases/tag/v0.7.0).

## Download

Two packages, same app:

- **AIMeter-script.zip** (recommended): unzip and run `Start AI Meter.cmd`. It works with **Smart App Control** on: Windows PowerShell, signed by Microsoft, compiles and starts the app from source.
- **AIMeter.zip**: the executable. Unzip and run `AIMeter.exe`. Releases are not signed yet, so Smart App Control blocks it.

Requires Windows 10 or 11 (.NET Framework 4.8, already part of Windows). Upgrading: exit AI Meter from its menu, replace the files, start it again; your settings are kept.

Built by GitHub Actions from the tagged source of this repository.
[Code signing policy](https://github.com/dichiara19/ai-meter#code-signing-policy) · [Privacy policy](https://github.com/dichiara19/ai-meter#privacy-policy)
