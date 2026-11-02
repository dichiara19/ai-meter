# Publishing to the Microsoft Store

The Store signs the MSIX package with a Microsoft certificate, so the installed app is trusted by SmartScreen and Smart App Control, and it updates automatically. The CI workflow builds an unsigned `AIMeter.msix` (artifact **AIMeter-msix**) that is uploaded as is: the Store signs it.

## One-time setup

1. Create a free individual developer account in [Partner Center](https://partner.microsoft.com/dashboard/registration) (identity check with an ID document and a selfie).
2. **Apps and games → New product → MSIX or PWA app**, reserve the name **AI Meter**.
3. Open **Product management → Product identity** and copy the three values into the repository variables (*Settings → Secrets and variables → Actions → Variables*):

   | Variable | Partner Center field |
   |---|---|
   | `STORE_IDENTITY_NAME` | Package/Identity/Name |
   | `STORE_PUBLISHER` | Package/Identity/Publisher (`CN=…`) |
   | `STORE_PUBLISHER_NAME` | Package/Properties/PublisherDisplayName |

   Without them the package is built with placeholder values that the Store rejects.

## Each release

Only **stable** tags (`vX.Y.Z`) go to the Store; betas and release candidates stay on GitHub ([RELEASING.md](RELEASING.md)). The package version is the stable version plus `.0`, so it never changes between a beta and its stable release: never submit an MSIX built from a pre-release tag.

- **With automatic submission configured** (below): push the stable tag. After the GitHub release is published, the `store` job uploads `AIMeter.msix` to a new submission and commits it for certification. Check its status in Partner Center.
- **Without it**: after the stable tag's run, download its **AIMeter-msix** artifact, create a submission in Partner Center and upload `AIMeter.msix` under **Packages**.

The first submission is always made by hand: Microsoft's tools can only update an app that is already published.

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

The Store installs updates by itself, and Store policy does not allow an app to update itself any other way. The packaged app therefore never contacts GitHub for updates and never downloads anything (`UpdateCheck.Find` returns before any request when `Native.IsPackaged`, and a regression test holds it to that).

Instead, with **Settings → About → Check for updates** on, once a day it asks the Store, through Windows' `StoreContext.GetAppAndOptionalStorePackageUpdatesAsync`, whether an update is waiting, and if so shows *An update is waiting in the Microsoft Store* with a button that opens the Store's downloads page. This uses the Windows metadata already referenced by the MSIX build (`Windows.Services.winmd`), with no new dependency. It only answers for a copy installed from the Store; a sideloaded package reports nothing. It has not been verified on a Store install yet: check it on the first private-audience submission.

## Submission answers

- **Category:** Developer tools (or Utilities & tools).
- **Privacy policy URL:** `https://github.com/dichiara19/ai-meter#privacy-policy`
- **Restricted capability `runFullTrust`, justification:**
  > AI Meter is a desktop app (WPF on .NET Framework) packaged as MSIX. It needs full trust to read the local session files written by Claude Code, Codex and Cursor, to run the user's installed `codex` and `claude` command-line tools to read their own usage limits, and to place its compact indicator inside the Windows taskbar. With the user's consent it also reads two kinds of credentials that other programs already store on the PC: the OAuth access token of the user's Claude Code login, sent only to Anthropic's usage endpoint, and API keys saved by OpenCode, Pi or Crush or exported in shell profiles, each sent only to its own service's usage endpoint. Both are asked on the first-launch screen (API keys are off unless the user turns them on), can be turned off in Settings, are never stored by AI Meter and are never sent to its authors. It does not install drivers or services and does not require administrator rights.
- **Start at login:** the app declares a startup task. It is turned on at first launch, as in the other builds, and the user can turn it off in AI Meter's Settings or in Windows Settings → Apps → Startup.
- **Taskbar indicator:** placed only after the user confirms *Show in the taskbar* on the first-launch screen; otherwise the app shows a desktop widget. It can be switched off at any time from the notification area menu or Settings → Where to see it.
- **Notes for certification:** no account is needed to test the app. On the first-launch screen choose **Preview with sample data**, or later turn on Settings → About → **Sample data**: every surface then shows invented figures for Claude, Codex, Copilot and local models. Real data needs at least one of Claude Code, Codex, GitHub Copilot or Cursor signed in; without them the app shows the services as not connected, which is the expected behaviour.

Tip: publish the first submission with **Visibility → Private audience** to install it from the Store on your own PC and check it before making it public.

## Packaged mode differences

- Start with Windows uses the package's startup task instead of the "AI Meter" shortcut in the Startup folder used by the other builds.
- The taskbar identity comes from the package, so the per-window AppUserModel properties are not set.
- `%APPDATA%\AIMeter` is redirected by Windows to the package's private folder; settings and diagnostic files live there and are removed when the app is uninstalled.
- No GitHub update check: the Store updates the package, and Settings → About only shows whether the Store has an update waiting.
