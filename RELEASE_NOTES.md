## What changed in 0.10.0-rc.1

This is a release candidate for 0.10.0. It is published as a pre-release: the update notice shows it only if you opt in to betas and release candidates in **Settings → About**. Please report anything that looks wrong.

### Clearer numbers
- **Every value says what is left, in its own unit.** Quotas read "18% left", credit limits "$0.73 left of $15.00" (just "$0.73" in the taskbar pill), and local models' video memory "4.6/16 GB" free out of the total. The same wording appears in the pill, the panel, the widget, the overlay, tooltips and the notification-area menu. Bars match the number shown.
- **Your choice.** In **Settings → Where to see it** you can show what is used instead of what is left, or show credit limits as a percentage. Notifications always say what is left, since they warn that a limit is running out.
- **Logos without the apps.** When Copilot or ChatGPT/Codex is not installed, AI Meter shows the official logo that GitHub and OpenAI publish to identify their services. Claude and any other service without a logo show their initials.

### New services and agents
- **Cloudflare Workers AI:** the Neurons used today against the free 10,000 a day, which reset at midnight UTC. *Create a key* opens Cloudflare's token page with the read-only analytics permission already filled in. The token and account ID can also come from OpenCode, Pi or Hermes. *Tested against recorded answers; reports from real accounts are welcome.*
- **Hermes Agent:** its recent sessions appear among the coding agents, its `.env` keys are found like those of OpenCode and Pi, and **Settings → AI apps** copies its `hermes mcp add` command.
- **NVIDIA API Catalog and Gemini API:** these services cannot report usage or limits. They are listed in Settings as model catalogues for the AI apps you connect, never on the taskbar, panel or widget.
- **Antigravity CLI** replaces the discontinued Gemini CLI in **Settings → AI apps**.

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

### Updates
- AI Meter now tells stable releases from pre-releases. The update notice offers only stable versions, unless you opt in to betas and release candidates in **Settings → About**.

### Also
- The copy buttons in **Settings → AI apps** confirm on the button itself.
- Many fixes from code reviews: lease times, CLI discovery, failing services, the Codex `config.toml` editor and the MCP pipe host.

## Download

- **AIMeter-script.zip** (recommended): unzip and run `Start AI Meter.cmd`. Works with Smart App Control enabled.
- **AIMeter.zip**: unzip and run `AIMeter.exe`. The executable is unsigned and may be blocked by Smart App Control.

To update, exit AI Meter, replace the files in its existing folder and start it again. Settings and usage history are retained. Requires Windows 10 or 11 with .NET Framework 4.8.
