<p align="center"><img src="https://raw.githubusercontent.com/dichiara19/ai-meter/v0.7.2/docs/media/hero-taskbar.png" alt="The AI Meter pill inside the Windows 11 taskbar, with its tooltip" width="720"></p>

## What's new in 0.7.2

- **Spending estimates are explicit.** Readings of cumulative usage or balances show an estimate and when observation started. A first reading no longer appears as zero spending, and consumption from an earlier day is not charged to today. Estimates cannot establish remaining budget or trigger forecasts and threshold alerts; provider-reported period totals still support those features.
- **Accounts and periods stay separate.** Changing an API key starts a separate spending baseline and clears the previous key's cached credits, forecast samples and alert state. Yesterday's provider totals are not reused for a new daily, weekly or monthly budget. Legacy spending history without a credential identity is not used as a baseline.
- **Pi forks count paid replies once.** Responses inherited by forked or cloned sessions no longer double model costs, tokens or reply counts.
- **OpenRouter credits survive temporary failures.** The last successful extra response stays visible as cached data, without live bars or alerts. Pill preferences survive missing data and restarts, with a ten-day grace period before removing a limit that is no longer reported. Rejected management keys respect the retry delay; replacing a key retries immediately.
- **Automatic includes monetary limits.** The pill can select spending budgets and credit limits, while excluding stale readings and estimates. Monetary amount changes also refresh the display even when the rounded percentage stays the same.
- **Regression coverage.** 24 isolated tests now run in CI before the build. Comments in the revised source files are in English.

Earlier changes are in the [0.7.1 notes](https://github.com/dichiara19/ai-meter/releases/tag/v0.7.1) and [0.7.0 notes](https://github.com/dichiara19/ai-meter/releases/tag/v0.7.0).

## Download

Two packages, same app:

- **AIMeter-script.zip** (recommended): unzip and run `Start AI Meter.cmd`. It works with **Smart App Control** on: Windows PowerShell, signed by Microsoft, compiles and starts the app from source.
- **AIMeter.zip**: the executable. Unzip and run `AIMeter.exe`. Releases are not signed yet, so Smart App Control blocks it.

Requires Windows 10 or 11 (.NET Framework 4.8, already part of Windows). Upgrading: exit AI Meter from its menu, replace the files, start it again; your settings are kept.

Built by GitHub Actions from the tagged source of this repository.
[Code signing policy](https://github.com/dichiara19/ai-meter#code-signing-policy) · [Privacy policy](https://github.com/dichiara19/ai-meter#privacy-policy)
