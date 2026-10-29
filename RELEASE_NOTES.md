## What changed in 0.8.3

A new installation now opens a welcome screen that asks before AI Meter does anything beyond reading local files:

- **Show in the taskbar.** The indicator is placed inside the Windows taskbar only if you keep this on; otherwise AI Meter shows a widget on the desktop.
- **Real-time limits.** Whether to read Claude limits from Anthropic with your existing Claude Code login.
- **Use API keys already on this PC.** Looking for keys saved by OpenCode, Pi or Crush, or exported in shell profiles, is now off until you turn it on.

Each choice can be changed later in Settings. Existing installations keep their current settings and do not see the screen.

**Sample data** can now be switched on without restarting, from the welcome screen or Settings → About, and switched off from the notification area menu. It shows invented figures for previews and is never saved, recorded in the history or used for notifications.

The Microsoft Store submission notes describe exactly which credentials are read and where each is sent. The regression suite contains 76 tests.

## Download

- **AIMeter-script.zip** (recommended): unzip and run `Start AI Meter.cmd`. Works with Smart App Control enabled.
- **AIMeter.zip**: unzip and run `AIMeter.exe`. The executable is unsigned and may be blocked by Smart App Control.

To update, exit AI Meter, replace the files in its existing folder and start it again. Settings and usage history are retained. Requires Windows 10 or 11 with .NET Framework 4.8.
