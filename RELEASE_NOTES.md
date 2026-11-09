## What changed in 0.10.1

- **The notification-area menu opens on every PC.** On some PCs with a touchscreen or pen, right-clicking the AI Meter icon showed an error window instead of the menu (#74). The cause was a pen or touch driver that reports one of its properties twice, which Windows' newer touch support in .NET cannot handle. AI Meter now checks the devices when it starts. On such a PC it uses the older touch support: the mouse and keyboard work as usual, and touch behaves as it did before 0.10.0. Any other unexpected error in the icon's menu is written to `errors.log` instead of showing an error window.

## What changed in 0.10.0

Everything since 0.8.6, including the 0.10.0-rc.1 release candidate and the fixes that followed it.

### Touch and full-screen apps
- **Touch.** A tap on the taskbar pill opens the panel at the first try, every list scrolls with a finger, press and hold shows a tooltip or the right-click menu, and buttons and rows are larger.
- **The taskbar pill stays out of full-screen apps.** It hides while a video, game or presentation fills the screen and comes back right after, just above the taskbar.

### Clearer numbers
- **Every value says what is left, in its own unit.** Quotas read "18% left", credit limits "$0.73 left of $15.00" (just "$0.73" in the taskbar pill), and local models' video memory "4.6/16 GB" free out of the total. The same wording appears in the pill, the panel, the widget, the overlay, tooltips and the notification-area menu. Bars match the number shown.
- **Your choice.** In **Settings → Where to see it** you can show what is used instead of what is left, or show credit limits as a percentage. Notifications always say what is left, since they warn that a limit is running out.
- **Logos without the apps.** When Copilot or ChatGPT/Codex is not installed, AI Meter shows the official logo that GitHub and OpenAI publish to identify their services. Claude and any other service without a logo show their initials.

### New services and agents
- **Cloudflare Workers AI:** the Neurons used today against the free 10,000 a day, which reset at midnight UTC, and what the Neurons beyond them cost today, this week and this month, so spending and budgets work as for OpenRouter. *Create a key* opens Cloudflare's token page with the read-only analytics permission already filled in. The token and account ID can also come from OpenCode, Pi or Hermes. An account ID copied together with the dashboard's address now works too.
- **Hermes Agent:** its recent sessions appear among the coding agents, and what it spent counts among today's models. Its `.env` keys are found like those of OpenCode and Pi, and **Settings → AI apps** copies its `hermes mcp add` command.
- **NVIDIA API Catalog and Gemini API:** these services cannot report usage or limits. They are listed in Settings as model catalogues for the AI apps you connect, never on the taskbar, panel or widget.
- **Antigravity CLI** replaces the discontinued Gemini CLI in **Settings → AI apps**, and its recent sessions appear among the coding agents with their title, status and model.

### Claude
- Signing in to Claude no longer opens a console window, and the desktop app's Claude Code is found.
- The Claude card shows your plan's monthly API credits.
- AI Meter keeps one data folder even when it is started from another app's package.

### Delegation (preview)
- **A plugin for Claude Code and Codex** hands self-contained tasks to a coding agent you already have (Pi or OpenCode) on a free model that AI Meter finds has room.
  - Agents are read-only by default, and their changes go to a new Git worktree.
  - API keys and tokens are kept away from the delegated agent.
  - A job counts as done only when its outcome is verified.
  - See the [plugin's README](https://github.com/dichiara19/ai-meter/blob/main/plugins/ai-meter/README.md).
- **AI apps can ask for fresh data:** MCP tools accept `max_age_seconds`.
- **Choosing where a task runs:**
  - `list_candidates` lists the free models a task can run on now, through which agent and with how much room.
  - `describe_dispatch` says how a plugin starts that agent.
- **Cloudflare Workers AI as a free tier:** with your consent, its models can take delegated tasks within the day's free Neurons. AI Meter grants each task a budget that keeps a reserve, and the plugin stops the agent when the task reaches it.

### Updates
- AI Meter now tells stable releases from pre-releases. The update notice offers only stable versions, unless you opt in to betas and release candidates in **Settings → About**.

### Also
- The copy buttons in **Settings → AI apps** confirm on the button itself.
- Many fixes from code reviews: lease times, CLI discovery, failing services, the Codex `config.toml` editor and the MCP pipe host.

## Download

- **AIMeter-script.zip** (recommended): unzip and run `Start AI Meter.cmd`. Works with Smart App Control enabled.
- **AIMeter.zip**: unzip and run `AIMeter.exe`. The executable is unsigned and may be blocked by Smart App Control.

To update, exit AI Meter, replace the files in its existing folder and start it again. Settings and usage history are retained. Requires Windows 10 or 11 with .NET Framework 4.8.
