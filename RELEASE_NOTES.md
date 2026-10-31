## What changed in 0.8.4

- **Automatic limit choice fixed.** With *Limit in the taskbar → Automatic*, the pill now shows the limit with the least room left. Before, a five-hour session forecast to run out took precedence even when the weekly limit was lower, as happened with ChatGPT. A forecast now only breaks ties between limits with the same room.
- **OpenRouter free models.** The OpenRouter card counts the models offered at no cost and lists those whose free period ends soonest, with the date. The tooltip shows every free model with its context window, inputs, tools and reasoning support. The list comes from OpenRouter's public model list, read every six hours while the service is on, and can be hidden in Settings → Services → OpenRouter → In the panel.
- **Experiential Labs free models.** The Experiential Labs card lists its free models the same way, read with your key. Models without a listed price are not counted as free.
- Media generators with zero token prices, such as music, image and video models, are no longer listed as free models. A failed model list is retried after 15 minutes instead of six hours.
- **OpenRouter free-model requests.** A new row shows today's free-model requests against the daily cap OpenRouter reports (50, or 1000 after buying at least 10 credits), resetting at midnight UTC ("resets tomorrow 01:00" in local time). It can be picked for the pill; the default pill limit is unchanged.
- Service definitions accept `{token}` in the `freeModels` request, and `period` (`day`, `week` or `month`, in UTC) for counters whose response has no reset time.

## Download

- **AIMeter-script.zip** (recommended): unzip and run `Start AI Meter.cmd`. Works with Smart App Control enabled.
- **AIMeter.zip**: unzip and run `AIMeter.exe`. The executable is unsigned and may be blocked by Smart App Control.

To update, exit AI Meter, replace the files in its existing folder and start it again. Settings and usage history are retained. Requires Windows 10 or 11 with .NET Framework 4.8.
