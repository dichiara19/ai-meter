## What changed in 0.8.2

Adaptive consumption alerts compare quota consumption with your locally observed history. Once enough comparable history is available, AI Meter suggests enabling optional notifications. An unusual episode shows the observed change and potentially associated sessions; the user decides whether the activity explains it. Session overlap does not establish responsibility for account-wide quota consumption.

![Unusual consumption with sample data](https://github.com/dichiara19/ai-meter/releases/download/v0.8.2/consumption-dark.png)

This release fixes the reference and episode lifecycle:

- Open episodes retain their original thresholds across day changes and restarts. Intervals overlapping unusual episodes do not train subsequent references.
- Sparse weekday/weekend history falls back to a usable general reference.
- Deleting history invalidates live reference caches and queued consumption notifications.
- Collection interruptions reset weak-departure and recovery counters.

Screenshots show real application controls with synthetic sample data. Detection thresholds remain calibration candidates; synthetic replay does not establish real-world accuracy. The regression suite contains 76 tests.

[Light theme screenshot](https://github.com/dichiara19/ai-meter/releases/download/v0.8.2/consumption-light.png) · [History and alert behaviour](https://github.com/dichiara19/ai-meter/blob/v0.8.2/docs/HISTORY.md)

## Download

- **AIMeter-script.zip** (recommended): unzip and run `Start AI Meter.cmd`. Works with Smart App Control enabled.
- **AIMeter.zip**: unzip and run `AIMeter.exe`. The executable is unsigned and may be blocked by Smart App Control.

To update, exit AI Meter, replace the files in its existing folder and start it again. Settings and usage history are retained. Requires Windows 10 or 11 with .NET Framework 4.8.
