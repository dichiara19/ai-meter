## What changed in 0.8.1

Launching another copy of AI Meter no longer redirects Windows startup to that copy. Existing shortcuts, entries disabled in Windows and legacy Run commands are preserved when the app opens, including on first use with an existing registration.

Installing with `build.ps1` updates the Start menu and existing startup registration together. Explicitly changing **Start with Windows** still applies to the copy currently running. The regression suite now includes 61 tests, with coverage for existing and disabled shortcuts, legacy commands, first use and script paths containing spaces.

## Download

- **AIMeter-script.zip** (recommended): unzip and run `Start AI Meter.cmd`. Works with Smart App Control enabled.
- **AIMeter.zip**: unzip and run `AIMeter.exe`. The executable is unsigned and may be blocked by Smart App Control.

To update, exit AI Meter, replace the files in its existing folder and start it again. Settings and usage history are retained. If you previously launched copies from different folders, reinstall with `build.ps1` to align the Start menu and startup shortcuts.

Requires Windows 10 or 11 with .NET Framework 4.8. Built by GitHub Actions from the tagged source.

[Usage history introduced in 0.8.0](https://github.com/dichiara19/ai-meter/releases/tag/v0.8.0) · [Privacy policy](https://github.com/dichiara19/ai-meter#privacy-policy)
