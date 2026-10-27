<p align="center"><img src="https://raw.githubusercontent.com/dichiara19/ai-meter/v0.6.0/docs/media/hero-taskbar.png" alt="The AI Meter pill inside the Windows 11 taskbar, with its tooltip" width="720"></p>

## What's new in 0.6.0

- **New Settings.** Built like Windows 11 Settings: a side menu (Services, Where to see it, Notifications, General, About) and a page for each service ("Services › Claude") with its sign-in, options and limit in the taskbar. The services list shows each one's status at a glance.
- **Vercel AI Gateway and Experiential Labs.** Remaining credits of your Vercel team and credits used and left on Experiential Labs, with an API key.
- **Official logos.** Cursor, DeepSeek, Kimi and OpenRouter show their logos even when no app is installed.
- **Model limits show the real room left.** A model's weekly limit (for example Week · Fable) counts toward the weekly limit for all models too, so AI Meter now shows the lower of the two and no longer suggests using a model limit the general one can't cover.

## Download

Two packages, same app:

- **AIMeter-script.zip** (recommended): unzip and run `Start AI Meter.cmd`. It works with **Smart App Control** on: Windows PowerShell, signed by Microsoft, compiles and starts the app from source.
- **AIMeter.zip**: the executable. Unzip and run `AIMeter.exe`. Releases are not signed yet, so Smart App Control blocks it.

Requires Windows 10 or 11 (.NET Framework 4.8, already part of Windows). Upgrading: exit AI Meter from its menu, replace the files, start it again; your settings are kept.

Built by GitHub Actions from the tagged source of this repository.
[Code signing policy](https://github.com/dichiara19/ai-meter#code-signing-policy) · [Privacy policy](https://github.com/dichiara19/ai-meter#privacy-policy)
