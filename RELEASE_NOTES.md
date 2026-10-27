<p align="center"><img src="https://raw.githubusercontent.com/dichiara19/ai-meter/v0.7.0/docs/media/hero-taskbar.png" alt="The AI Meter pill inside the Windows 11 taskbar, with its tooltip" width="720"></p>

## What's new in 0.7.0

- **API keys found automatically.** If OpenCode, Pi or Crush already use your OpenRouter, DeepSeek, Kimi, Vercel or Experiential Labs key, or it is exported in your shell profile, AI Meter uses it from there, on Windows and in WSL. Each key is sent only to its own service, and this can be turned off.
- **Budgets and spend per period.** See what you spent today, this week and this month, and set a spending budget per day, week or month with its bar and notifications.
- **Today's models.** The models your agents used today with each service, with cost and tokens, from OpenCode and Pi.
- **OpenRouter account credits.** The credits left on your account, with the same API key.
- **Choose what each card shows.** Every service page has an "In the panel" section to show or hide each row.
- **Model limits.** A model's weekly limit (for example Week · Fable) shows its own share as a faint bar behind the real room left.

## Download

Two packages, same app:

- **AIMeter-script.zip** (recommended): unzip and run `Start AI Meter.cmd`. It works with **Smart App Control** on: Windows PowerShell, signed by Microsoft, compiles and starts the app from source.
- **AIMeter.zip**: the executable. Unzip and run `AIMeter.exe`. Releases are not signed yet, so Smart App Control blocks it.

Requires Windows 10 or 11 (.NET Framework 4.8, already part of Windows). Upgrading: exit AI Meter from its menu, replace the files, start it again; your settings are kept.

Built by GitHub Actions from the tagged source of this repository.
[Code signing policy](https://github.com/dichiara19/ai-meter#code-signing-policy) · [Privacy policy](https://github.com/dichiara19/ai-meter#privacy-policy)
