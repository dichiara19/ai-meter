# Security policy

AI Meter reads sign-in tokens that other apps keep on your PC, and stores API keys you add in Settings. Security reports are taken seriously.

## Supported versions

Only the [latest release](https://github.com/dichiara19/ai-meter/releases/latest) receives security fixes.

## Reporting a vulnerability

**Please don't open a public issue.** Instead, report it privately through GitHub: go to the repository's **Security** tab and choose **Report a vulnerability** ([direct link](https://github.com/dichiara19/ai-meter/security/advisories/new)).

Please include:
- what an attacker could do;
- the steps to reproduce it;
- the version of AI Meter and of Windows.

Never include real tokens or keys.

You will get an answer within a week. Once the issue is fixed, the advisory is published, crediting you unless you prefer otherwise.

## Scope

Of particular interest:
- how tokens and API keys are read, stored and sent (only to the service they belong to, never refreshed or stored for other apps);
- loading of service definitions from `%APPDATA%\AIMeter\providers`;
- the optional managed Pi ([ADR 0004](docs/adr/0004-managed-pi-for-delegation.md), [ADR 0005](docs/adr/0005-managed-pi-inside-wsl.md)): the download of a pinned release (two archives, Windows and Linux) and its SHA-256 check, unpacking (inside WSL by the distribution's own `tar`, started through `wsl.exe` only during an installation the user asked for, with a configuration folder of mode 700), and the plain-text `auth.json` under `managed\pi\config` and, for the copy inside WSL, under `~/.aimeter/managed/pi/config` in the Linux home, that hold the keys the user chose to copy into them (AI Meter's own key store is encrypted with DPAPI; these files, like any Pi installation's, are not). It is offered through a copy only while the service is ticked, and is deleted when nothing is ticked, when the copy is not installed and at every start; inside WSL AI Meter reaches the files only while the distribution runs, and otherwise leaves them as they are. **Known limit:** Pi's tools are not confined to the task's folder, so like any coding agent run as the user (the user's own Pi and its `~/.pi` included) a copy can read its file, and a model that is told to, or tricked into it, can see all the keys copied there, not only that of the service the task runs on. The plugin removes keys from the answers it returns, but the model's service has then already received it. Copy only keys whose free-tier exposure you accept, and delegate no task on content you do not trust;
- requests to local model servers on `127.0.0.1`;
- the MCP server: the per-user named pipe, the `mcp.ps1` relay, what its tools return, and the configuration files of other apps that it edits from Settings → AI apps;
- once they ship, delegation ([ADR 0002](docs/adr/0002-delegation-through-coordinator-plugins.md)): dispatch descriptions (no shell strings, no task text), capacity leases and what they store, and the coordinator plugins in `plugins/` (how they start agents, worktree isolation), and where credentials go;
- the build and release pipeline in `.github/workflows`.

Out of scope:
- issues that need an attacker who already has full control of the user's account on the PC;
- social engineering.
