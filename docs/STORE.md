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

1. Bump the version in `src/AssemblyInfo.cs` (the package version is the same plus `.0`).
2. Run the workflow (tag `v*` or *Run workflow*) and download the **AIMeter-msix** artifact.
3. In Partner Center, create a submission and upload `AIMeter.msix` under **Packages**.

## Submission answers

- **Category:** Developer tools (or Utilities & tools).
- **Privacy policy URL:** `https://github.com/dichiara19/ai-meter#privacy-policy`
- **Restricted capability `runFullTrust`, justification:**
  > AI Meter is a desktop app (WPF on .NET Framework) packaged as MSIX. It needs full trust to read the local session files written by Claude Code, Codex and Cursor, to run the user's installed `codex` and `claude` command-line tools to read their own usage limits, and to place its compact indicator inside the Windows taskbar. It does not install drivers or services and does not require administrator rights.
- **Start at login:** the app declares a startup task. It is turned on at first launch, as in the other builds, and the user can turn it off in AI Meter's Settings or in Windows Settings → Apps → Startup.
- **Notes for certification:** to see data, the tester needs at least one of Claude Code, Codex, GitHub Copilot or Cursor signed in; without them the app shows the services as not connected, which is the expected behaviour.

Tip: publish the first submission with **Visibility → Private audience** to install it from the Store on your own PC and check it before making it public.

## Packaged mode differences

- Start with Windows uses the package's startup task instead of the "AI Meter" shortcut in the Startup folder used by the other builds.
- The taskbar identity comes from the package, so the per-window AppUserModel properties are not set.
- `%APPDATA%\AIMeter` is redirected by Windows to the package's private folder; settings and diagnostic files live there and are removed when the app is uninstalled.
