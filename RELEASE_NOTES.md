<p align="center"><img src="https://raw.githubusercontent.com/dichiara19/ai-meter/v0.4.0/docs/media/hero-taskbar.png" alt="The AI Meter pill inside the Windows 11 taskbar, with its tooltip" width="720"></p>

## What's new in 0.4.0

- **Choose the limit each service shows.** In Settings → Services, pick which limit the pill, its tooltip and the overlay show for each service: the weekly limit instead of the 5-hour one, a model's weekly limit, or **Automatic**, the one closest to running out at your current pace.
- **A notification when a limit resets.** In Settings → Notifications, turn it on for any limit. It arrives at the time the service announced, and never hours late if the PC was off.
- **Use it or lose it.** A tip in the panel, and once as a notification, when a limit resets soon and at your usual pace a good part of it would go unused ("Week: 60% still unused, resets tomorrow at 09:00"). It works the same for 5-hour, weekly and monthly limits and for credits that expire. Another tip suggests waiting for a close reset instead of paying for extra usage.
- **Fixes.** The panel stays docked to the taskbar when its content changes height. Reset times that drift by a few minutes no longer repeat notifications. A limit choice that the service stops reporting goes back to Default.

## Download

Two packages, same app:

- **AIMeter-script.zip** (recommended): unzip and run `Start AI Meter.cmd`. It works with **Smart App Control** on: Windows PowerShell, signed by Microsoft, compiles and starts the app from source.
- **AIMeter.zip**: the executable. Unzip and run `AIMeter.exe`. Releases are not signed yet, so Smart App Control blocks it.

Requires Windows 10 or 11 (.NET Framework 4.8, already part of Windows). Upgrading: exit AI Meter from its menu, replace the files, start it again; your settings are kept.

Built by GitHub Actions from the tagged source of this repository.
[Code signing policy](https://github.com/dichiara19/ai-meter#code-signing-policy) · [Privacy policy](https://github.com/dichiara19/ai-meter#privacy-policy)
