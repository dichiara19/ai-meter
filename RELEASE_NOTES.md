<p align="center"><img src="https://raw.githubusercontent.com/dichiara19/ai-meter/v0.3.1/docs/media/hero-taskbar.png" alt="The AI Meter pill inside the Windows 11 taskbar, with its tooltip" width="720"></p>

## What's new in 0.3.1

- **The taskbar pill is the default view.** On a new install, AI Meter shows its pill inside the taskbar instead of the desktop widget. If the taskbar can't host it (for example a vertical taskbar), the desktop widget appears instead. Existing settings are kept, and the widget and the overlay are still one switch away in Settings.
- **Reset dates in the pill's tooltip.** Limits that reset on another day now say when ("resets on Sunday at 00:00"), not just the time.

Everything else new since 0.2.0 (local models, coding agents, forecast and notifications, services from JSON files) is in the [0.3.0 notes](https://github.com/dichiara19/ai-meter/releases/tag/v0.3.0).

## Download

Two packages, same app:

- **AIMeter-script.zip** (recommended): unzip and run `Start AI Meter.cmd`. It works with **Smart App Control** on: Windows PowerShell, signed by Microsoft, compiles and starts the app from source.
- **AIMeter.zip**: the executable. Unzip and run `AIMeter.exe`. Releases are not signed yet, so Smart App Control blocks it.

Requires Windows 10 or 11 (.NET Framework 4.8, already part of Windows). Upgrading: exit AI Meter from its menu, replace the files, start it again; your settings are kept.

Built by GitHub Actions from the tagged source of this repository.
[Code signing policy](https://github.com/dichiara19/ai-meter#code-signing-policy) · [Privacy policy](https://github.com/dichiara19/ai-meter#privacy-policy)
