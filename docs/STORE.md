# Publishing to the Microsoft Store

AI Meter is live in the Store as [AI Meter - Windows](https://apps.microsoft.com/detail/9PKMRQWBWHDM) (Store ID `9PKMRQWBWHDM`) since version 0.10.0.

The Store signs the MSIX package with a Microsoft certificate, so the installed app is trusted by SmartScreen and Smart App Control, and it updates automatically. The CI workflow builds an unsigned `AIMeter.msix` (artifact **AIMeter-msix**) that is uploaded as is: the Store signs it.

## One-time setup

1. Create a free individual developer account in [Partner Center](https://partner.microsoft.com/dashboard/registration) (identity check with an ID document and a selfie).
2. **Apps and games → New product → MSIX or PWA app**, and reserve the app's name. "AI Meter" is taken by another developer's app, so the product is **AI Meter - Windows**.
3. Open **Product management → Product identity** and copy its values into the repository variables (*Settings → Secrets and variables → Actions → Variables*), with the reserved name:

   | Variable | Partner Center field |
   |---|---|
   | `STORE_IDENTITY_NAME` | Package/Identity/Name |
   | `STORE_PUBLISHER` | Package/Identity/Publisher (`CN=…`) |
   | `STORE_PUBLISHER_NAME` | Package/Properties/PublisherDisplayName |
   | `STORE_DISPLAY_NAME` | The reserved app name, `AI Meter - Windows`: the Store accepts only a package whose display name is a reserved name |

   Without them `build.ps1` uses placeholder values that the Store rejects. They are fine for local and pull-request builds, but on a `v*` tag the workflow's MSIX step fails and names the missing variables.

## Each release

Only **stable** tags (`vX.Y.Z`) go to the Store; betas and release candidates stay on GitHub ([RELEASING.md](RELEASING.md)). The package version is the stable version plus `.0`, so it never changes between a beta and its stable release: never submit an MSIX built from a pre-release tag.

- **With automatic submission configured** (below): push the stable tag. After the GitHub release is published, the `store` job uploads `AIMeter.msix` to a new submission and commits it for certification. Check its status in Partner Center.
- **Without it**: after the stable tag's run, download its **AIMeter-msix** artifact, create a submission in Partner Center and upload `AIMeter.msix` under **Packages**.

The first submission is always made by hand: Microsoft's tools can only update an app that is already published.

Every build runs the [Windows App Certification Kit](https://learn.microsoft.com/windows/uwp/debug-test-perf/windows-app-certification-kit), the technical tests the Store runs during [certification](https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/app-certification-process), on its `AIMeter.msix` (the `certify` job; report in the **AIMeter-wack** artifact). The job fails when the overall result, which counts only the required tests, is not PASS. The optional *Blocked executables* test always fails, because AI Meter starts `cmd`, PowerShell and `wsl.exe` by design (sign-ins, the MCP relay); [optional tests](https://learn.microsoft.com/windows/uwp/debug-test-perf/windows-desktop-bridge-app-tests#required-versus-optional-tests) are informational and do not count for the Store. Do not submit a package whose `certify` job failed.

## Automatic submission

The workflow uses Microsoft's [Store Developer CLI](https://learn.microsoft.com/windows/apps/publish/msstore-dev-cli/overview) through the official [`microsoft/microsoft-store-apppublisher`](https://learn.microsoft.com/windows/apps/publish/msstore-dev-cli/github-actions) action: `msstore reconfigure` with Partner Center API credentials, then `msstore publish AIMeter.msix -id <product id>`. The job stays off until all of this is set:

1. Publish the first version by hand and wait until it is live in the Store.
2. In Partner Center, associate a Microsoft Entra ID tenant with the account (*Account settings → Tenants*), [register an application](https://learn.microsoft.com/entra/identity-platform/quickstart-register-app) in that tenant, then add it under *Account settings → User management → Microsoft Entra applications* with the **Manager** role. Create a client secret for it.
3. Repository **secrets** (*Settings → Secrets and variables → Actions → Secrets*), with the names Microsoft's guide uses:

   | Secret | Value |
   |---|---|
   | `AZURE_AD_TENANT_ID` | Entra tenant ID |
   | `AZURE_AD_APPLICATION_CLIENT_ID` | Application (client) ID of the registered app |
   | `AZURE_AD_APPLICATION_SECRET` | its client secret (they expire: renew it before the date Entra shows) |
   | `SELLER_ID` | Seller ID (also shown as Publisher ID) from Partner Center *Account settings → Developer settings / Identifiers* |

4. Repository **variable** `STORE_PRODUCT_ID`: the Store ID of the app (12 characters, such as `9NXXXXXXXXXX`, under *Product management → Product identity*). The job runs only when this variable and `STORE_IDENTITY_NAME` are set.
5. Optional: add required reviewers to the `microsoft-store` environment (*Settings → Environments*) to approve each submission by hand before it starts.

The CLI recreates the submission from the last published one, so the listing, screenshots and *What's new* text stay as they were; edit them in Partner Center when they need to change. Microsoft supports this flow for free apps only, which AI Meter is.

## Updates in the Store build

The Store installs updates by itself, and Store policy does not allow an app to update itself any other way. The packaged app therefore never contacts GitHub for updates and never downloads an update of itself (`UpdateCheck.Find` returns before any request when `Native.IsPackaged`, and a regression test holds it to that).

The packaged app makes no download at all today. The optional managed Pi ([below](#optional-download-managed-pi)) is switched off in the Store build (`OfferedInStoreBuild` is `false`) until the listing discloses it; once on, it will be the only download the packaged app can make (one of two archives, as the user chooses), which is not an update of AI Meter and happens only when the user presses its button in Settings.

Instead, with **Settings → About → Check for updates** on, once a day it asks the Store, through Windows' `StoreContext.GetAppAndOptionalStorePackageUpdatesAsync`, whether an update is waiting, and if so shows *An update is waiting in the Microsoft Store* with a button that opens the Store's downloads page. This uses the Windows metadata already referenced by the MSIX build (`Windows.Services.winmd`), with no new dependency. It only answers for a copy installed from the Store; a sideloaded package reports nothing. It has not been verified on a Store install yet: check it when the next version reaches the Store.

## Submission answers

- **Category:** Developer tools (or Utilities & tools).
- **Privacy policy URL:** `https://github.com/dichiara19/ai-meter#privacy-policy`
- **Restricted capability `runFullTrust`, justification:**
  > AI Meter is a desktop app (WPF on .NET Framework) packaged as MSIX. It needs full trust for the following, always as the signed-in user and without administrator rights:
  > - Read the local session and usage files that Claude Code, Codex, GitHub Copilot, Cursor and other AI tools already write on the PC, and place its compact indicator inside the Windows taskbar.
  > - Run the user's installed command-line tools: `codex` and `claude` to read their own usage limits; `wsl.exe` to list running WSL distributions (`wsl.exe -l --running`, which starts nothing) and to run `claude` or `agy` inside a WSL distribution that is already running, only to renew an expired sign-in; `cmd.exe` or `wsl.exe` to open a terminal with `claude auth login` or `agy` when the user clicks *Sign in*.
  > - Use sign-ins the user already has: the OAuth access token of the Claude Code login (sent only to Anthropic's usage endpoint), the Cursor app's sign-in (sent only to Cursor), the Antigravity CLI sign-in (sent only to Google) and, when the user chooses *Sign in* for Copilot, a GitHub device-flow sign-in whose token is kept encrypted with Windows DPAPI. With the user's consent, API keys saved by OpenCode, Pi or Crush or exported in shell profiles, each sent only to its own service's usage endpoint. The Claude login and API keys are asked on the first-launch screen (API keys are off unless the user turns them on); every service can be turned off in Settings. Nothing is sent to AI Meter's authors.
  > - When the user turns an app on in Settings → MCP → Connected apps, add an `ai-meter` entry to that app's MCP configuration (Claude Desktop, VS Code, Claude Code, Codex, Cursor, OpenCode, Pi or Antigravity CLI, on Windows or in WSL), keeping a backup of the previous file; *Disconnect* removes it. The entry starts a relay script that AI Meter writes to `%USERPROFILE%\.aimeter\mcp.ps1`, outside the package so that the other apps can read it; the script starts AI Meter through its execution alias when it is not running.
  >
  > It does not install drivers or services.
- **Start at login:** the app declares a startup task, disabled in the manifest. The first-launch screen has a *Start with Windows* switch (on by default, as in the other builds); the startup task is requested only after the user closes that screen, and only if the switch is on. The user can turn it off later in AI Meter's Settings or in Windows Settings → Apps → Startup. Someone moving over from the GitHub copy keeps its settings (both use `%APPDATA%\AIMeter`), but not its start-up, which is a shortcut the GitHub uninstaller removes. So the package shows the first-launch screen once more, with the other choices as they were, when it has never shown it itself and its startup task is still disabled (`PackageFirstRunDone`). When Windows cannot tell the task's state, nothing is recorded and the package looks again at the next launch.
- **Taskbar indicator:** placed only after the user confirms *Show in the taskbar* on the first-launch screen; otherwise the app shows a desktop widget. It can be switched off at any time from the notification area menu or Settings → Where to see it.
- **Notes for certification:** no account is needed to test the app. On the first-launch screen choose **Preview with sample data**, or later turn on Settings → About → **Sample data**: every surface then shows invented figures for Claude, Codex, Copilot and local models. Real data needs at least one of Claude Code, Codex, GitHub Copilot or Cursor signed in; without them the app shows the services as not connected, which is the expected behaviour.

Tip: publish the first submission with **Visibility → Private audience** to install it from the Store on your own PC and check it before making it public.

## Store listing

The package declares one language, `en-us`, so Partner Center asks for one listing, in English (policy 10.7 requires a listing for every language a package declares). The app still follows the Windows display language, or the one chosen in Settings → General, in English, Italian, Spanish, French, German and Portuguese: its strings are compiled into the executable and do not depend on the package's languages.

**Description.** Policy 10.2.4 requires the dependency to be disclosed at the start. The listing below is the current one; the managed Pi's disclosure is not part of it yet ([below](#optional-download-managed-pi)).

> AI Meter shows the usage of AI tools you already have: it needs at least one of Claude Code, Codex (ChatGPT), GitHub Copilot, Cursor, Antigravity CLI or a local model server such as Ollama or LM Studio, installed and signed in on this PC, or an API key of a supported service. It reads the data those tools save on your PC and is not affiliated with Anthropic, OpenAI, GitHub, Microsoft, Cursor or Google.
>
> A small pill inside the Windows taskbar shows how much of your AI limits is left: 5-hour and weekly limits, a forecast of when you will run out at the current pace, and a notification before you hit the wall that tells you which service still has room. Click it for every service, the coding agents that are running and the local models in video memory, on Windows and in WSL.
>
> • Lives in the taskbar, follows light and dark mode and hides with full-screen apps; a desktop widget and a compact overlay are there if you prefer.
> • A local history of quota cycles, daily activity, models and tools.
> • Claude, ChatGPT / Codex, GitHub Copilot, Google Antigravity, Cursor, OpenRouter, Vercel AI Gateway, DeepSeek, Kimi and more; a new service is a small definition file.
> • Your AI apps can ask it, through MCP, how much room your limits have.
> • Private: no telemetry and no account; the only network requests go to each service's own usage endpoint. Open source under Apache 2.0.
> • Six languages: English, Italian, Spanish, French, German and Portuguese.

### Optional download: managed Pi

From Settings › MCP › Delegation agent, the user can have the app download Pi ([ADR 0004](adr/0004-managed-pi-for-delegation.md), [docs/MCP.md](MCP.md#managed-pi)): one pinned release of a third-party open-source program (MIT) in two archives from `github.com`, each checked against a SHA-256 kept in AI Meter's sources: `pi-windows-x64.zip` (59 MB, about 131 MB unpacked) for Windows and `pi-linux-x64.tar.gz` (59 MB, about 125 MB unpacked) for the WSL distribution the app is set to, each with its own button ([ADR 0005](adr/0005-managed-pi-inside-wsl.md)). These are the only downloads there ever are: Pi itself runs with `PI_OFFLINE=1` and fetches nothing while it works. The copy on Windows would be unpacked to `%USERPROFILE%\.aimeter\managed\pi`, outside the package and outside `PATH`, because a packaged app's AppData is private to the package. The copy inside WSL is unpacked in the Linux home (`~/.aimeter/managed/pi`) by the distribution's own `tar`, which the app starts through `wsl.exe` only during an installation the user asked for, and only while the distribution already runs; the app never starts WSL. The user's keys reach each copy only for the services the user ticks.

**The Store build does not offer it yet.** `OfferedInStoreBuild` in `src/ManagedPi.cs` is `false`: the build shows no download button and offers no managed copy, on Windows or inside WSL. The release workflow submits the package automatically from a stable tag, while the listing text is edited by hand in Partner Center, so the package must not reach certification ahead of its disclosure. To enable it:

1. Update the listing description in Partner Center with the disclosure paragraph below, change the *Private* bullet to end "... usage endpoint and, only if you ask for it, the download of Pi.", and add certification notes saying where the button is (Settings › MCP › Delegation agent).
2. Only in the release after that, set `OfferedInStoreBuild` to `true`.
3. If certification refuses it, set `OfferedInStoreBuild` back to `false` and remove the disclosure paragraph from the description in the same submission. The GitHub builds are unchanged and the Store build keeps "use the agent you have".

Disclosure paragraph, to add at the start of the description when the feature is enabled in the Store build (not part of the current listing):

> Optional download: if you ask for it in Settings, AI Meter downloads Pi, a third-party open-source coding agent (MIT licence, about 59 MB for each of two builds, from GitHub), so that delegated tasks can run on Windows or inside WSL without installing an agent yourself. For the build inside WSL, AI Meter runs the distribution's own tar once to unpack it, while WSL is already running. Nothing is downloaded unless you press a download button, and you can remove each copy there.

The clauses it is meant to satisfy are Store Policies 7.20: 10.2.2, 10.2.3, 10.2.4 and 10.1.5. That is this project's reading of the policy, not a certification result. Re-read those clauses before each submission.

**Short description:** AI limits in your taskbar, and a local history of what you use, for the AI tools already on your PC.

**Screenshots:** six 3840x2160 slides per listing language, each a headline beside a scene of the real interface with demo data (taskbar pill, notification, panel, local models, usage history, light and dark). The Store shows desktop screenshots in a 16:9 frame, so other sizes get grey bands at the sides. Render them with

```powershell
powershell -ExecutionPolicy Bypass -File tools\render-store.ps1
```

which writes `bin\store-screenshots\<language>\1-taskbar.png` to `6-light-and-dark.png` for English, Italian, Spanish, French, German and Portuguese (`-Languages it,en` for a subset). Upload each language's folder, in order, to that language's listing: the first image is the one shown in search results. The headlines are in `tools/StoreScreenshots.cs`; keep them true to what the app does. The images are not committed: render them again when the interface changes.

**Store logo:** `bin\StoreIcon-300.png`, written by `build.ps1 -NoInstall -Package` (the app icon's rings, 300x300, transparent background).

## Age rating (IARC questionnaire)

Category **All other app types** (not a game, not a social or communication app). Every content question is answered **No**:

- Violence, blood, fear or horror: none.
- Sexual content or nudity: none.
- Crude humour, profanity or discriminatory language: none.
- Drugs, alcohol or tobacco: none.
- Gambling, simulated gambling or real-money purchases: none.
- Users can interact or exchange content (chat, posting, sharing): no. The app shows only the user's own data from their own tools.
- Shares the user's location or personal information with third parties: no. Requests go only to each service's usage endpoint, with the user's own sign-in.
- Unrestricted internet access through the app (a browser): no. Links open in the user's browser.
- Digital purchases: none.

Expected result: the lowest rating in every region (for example PEGI 3, ESRB Everyone).

## Testing the package locally

The Windows SDK (`makeappx`, `makepri`) is needed only to produce `AIMeter.msix`; the unpacked layout in `bin\msix` can be registered without it:

1. Turn on **Developer Mode** (Windows Settings → System → For developers).
2. `powershell -ExecutionPolicy Bypass -File build.ps1 -NoInstall -Package` (without the SDK it stops after writing `bin\msix`, with the warning *Windows SDK not found*).
3. `Add-AppxPackage -Register bin\msix\AppxManifest.xml`, then start **AI Meter** from the Start menu.
4. Check the first-launch screen and *Start with Windows*; Settings → MCP → Connected apps for Claude Desktop and VS Code, with and without their configuration file (an existing file must change in the real `%APPDATA%` folder; a missing one must be explained, not created); an AI app starting AI Meter through `%LOCALAPPDATA%\Microsoft\WindowsApps\AIMeter.exe` while AI Meter is closed; Settings → About → *Open data folder*.
5. Remove it with `Get-AppxPackage *AIMeter* | Remove-AppxPackage`.

Without the `STORE_*` values the registered package has the placeholder identity, so it installs next to, not over, a copy from the Store.

## Packaged mode differences

- Start with Windows uses the package's startup task instead of the "AI Meter" shortcut in the Startup folder used by the other builds.
- The taskbar identity comes from the package, so the per-window AppUserModel properties are not set.
- The package uses Windows' standard AppData write virtualisation; it declares no `unvirtualizedResources` (Partner Center refused that capability on the first submission, November 2026). A file AI Meter creates in `%APPDATA%` lands in the package's private copy, `%LOCALAPPDATA%\Packages\<family name>\LocalCache\Roaming`, which only AI Meter sees in place and which Windows removes on uninstall; a file that already existed is changed in place (a CI probe confirmed both, including `File.Copy` and `File.Replace` over an existing file). So:
  - Settings and diagnostic files are written to `%APPDATA%\AIMeter` as in the other builds and therefore land in the private copy, removed with the app. Files left there by another build are read and updated in place, and kept on uninstall. Settings → About → *Open data folder* opens the private copy when it exists (`Settings.ExplorerFolder`).
  - Claude Desktop and VS Code keep their MCP configuration in `%APPDATA%`. *Connect* edits the file when it exists; when it does not, the Store build creates nothing and tells the user how to have the app create it (Claude Desktop: Settings → Developer → Edit Config; VS Code: *MCP: Open User Configuration*), with the entry to paste by hand. The backup of the previous file lands in the private copy.
- AI apps start AI Meter through the execution alias `%LOCALAPPDATA%\Microsoft\WindowsApps\AIMeter.exe` (`Program.LaunchCommand`), which survives Store updates. The relay script is `%USERPROFILE%\.aimeter\mcp.ps1`, since other apps cannot read a package's private folders. Disconnect AI apps in Settings → AI apps before uninstalling, or their `ai-meter` entries point at a missing alias.
- The package declares only English as its language ([Store listing](#store-listing)); the interface languages are unchanged.
- No GitHub update check: the Store updates the package, and Settings → About only shows whether the Store has an update waiting.
- The managed Pi, once the Store build offers it and the user installs it, lives on Windows in `%USERPROFILE%\.aimeter\managed\pi` for the same reason as the relay script (the copy inside WSL is in the Linux home, outside the package): other programs cannot read a package's private folders. Removing the app does not remove it; the *Remove* button in Settings does, together with the keys copied into it.
