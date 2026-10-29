# Large-file refactoring plan

Working notes for splitting the four largest source files without changing behaviour. Delete this file once the work is merged.

Line numbers refer to commit `4c96a14`. Every proposed move is cut-and-paste: the only edits are adding `partial` and copying the `using` lines. Build and run `tests/run.ps1` after each file.

## Constraints

- Every compile path globs `src\*.cs` without recursion (`AIMeter.ps1:29`, `build.ps1:109,233`, `tests/run.ps1:13`, `tools/render-history.ps1:15`, `tools/render-media.ps1:17`, `.github/workflows/build.yml:67`). Put new files directly in `src/`, never in a subfolder.
- Tests reach private members by name through reflection, so moving them is safe but renaming them fails only at test time:
  - `DefinedProvider`: fields `D`, `live`, `spend`, `spendScope`; methods `ReadSpend`, `Decorate`, `Fetch`, `Limit`; nested `SpendIn.At` (`tests/RegressionTests.cs`).
  - `Controller`: `sessionLocked`, `suspended`, `paused`, `UpdateCollectionPause` (`tests/HistoryRegressionTests.cs:338-341`).
  - `Settings` property (`tools/HistoryPreview.cs:26`).
- `AIMeter.ps1:33` calls `[AIMeter.Entry]::Run`: keep that namespace and class name.
- Partial parts: put `static`/`sealed`, the base class and the doc comment on one part only. Keep instance field initialisers in the main part.
- Source files are UTF-8 without a BOM and contain non-ASCII glyphs (Segoe icons, `·`, `€`, `…`): keep the encoding.
- Update the file maps in `README.md` (around lines 172-192) and `docs/ARCHITECTURE.md:9`.

## `src/ProviderDefined.cs` (989 lines): separate classes, no `partial` needed

| New file | Lines | Content |
| --- | --- | --- |
| `ServiceDefinition.cs` | 18-206 | `ServiceDefinition`, `Definitions` |
| `JPath.cs` | 208-292 | `JPath` |
| `ApiKeys.cs` | 294-320 | `ApiKeys` |
| `ApiKeyWindow.cs` | 818-887 | `ApiKeyWindow` |
| `Sqlite.cs` | 889-988 | `Sqlite` (also used by CodingAgents, Spend and HistoryCollector) |
| `ProviderDefined.cs` | 322-816 | `DefinedProvider` only |

Optionally move response mapping (713-815) to a `partial` `DefinedProvider.Mapping.cs`; `Number` and `Currency` are shared with `ReadSpend`.

## `src/SettingsWindow.cs` (806 lines): `partial class SettingsWindow`

| New file | Lines |
| --- | --- |
| `SettingsWindow.cs` | 15-197, `Changed` 588, `Rebuild` 630-631, `UpdateStats` 633-645 |
| `SettingsWindow.Services.cs` | 199-443, 577-586, 590-628 |
| `SettingsWindow.Pages.cs` | 445-575, `WslDistros` 790-804 |
| `SettingsWindow.Controls.cs` | 646-788 (`Section`, `Hint` and `Group` write to `page`, so they must stay instance members) |

## `src/Ui.cs` (1037 lines): `internal static partial class Ui`

| New file | Lines |
| --- | --- |
| `Ui.cs` | 21-46, 199-342, 492-570 (except 513-514) |
| `UiLogos.cs` | 22 (`Logos`), 48-197 |
| `UiPill.cs` | 344-490 |
| `UiCards.cs` | 513-514 (`RowHidden`), 572-977 (includes `TextElement_Foreground` at 637) |
| `UiWindows.cs` | 979-1035 |

## `src/App.cs` (878 lines)

| New file | Lines |
| --- | --- |
| `Program.cs` | 18-141: `Entry` and `Program` (separate classes; keep `#if MSIX`) |
| `Controller.cs` | fields 145-183, 185-425, `Exit` 819-829, system events 840-875 |
| `ControllerSignIn.cs` | 427-486, 520-555, fields `githubLogin` (437) and `loginWatch` (173) |
| `ControllerSurfaces.cs` | 488-518, 557-648 |
| `ControllerTray.cs` | 650-817, 831-838, fields `menuHost` (167) and `alerts` (154) |

## Behaviour-neutral clean-ups found while reading

Do these after the split, in separate commits. They have not been made on this branch because they change code that could not be compiled or tested here.

- Rename `ColorLight`, `ColorDark`, `ParseColor` and the local `color` to British spelling (`ProviderDefined.cs:25,46-49,61`, `Theme.cs:83,98`). The JSON key `"color"` is a public contract and stays.
- Rename `TaskbarInfo.Centered` to `Centred` (`Taskbar.cs:20,28,40,44,55`). The translation key `"centered"` (`Taskbar.cs:55`, every table in `StringsTable.cs`) is a separate, cross-table change.
- Remove the unused `ApiKeyWindow(ServiceDefinition d)` constructor (`ProviderDefined.cs:823`; the only construction is `App.cs:455` with two arguments).
- Remove the unused local `src` in `Ui.MakeFluent` (`Ui.cs:1012`).
- `SettingsWindow.cs:53-54` repeat `FontFamily` and `UseLayoutRounding`, which `Ui.MakeSettingsWindow` already sets (`Ui.cs:983-984`).
- Duplicated logic worth a shared helper:
  - Bitmap decoding: `Ui.cs:69-76` and `103-109`.
  - Card header with chevron: `Ui.cs:695-708` and `877-890`.
  - `ToggleFlyout` / `ToggleFlyoutFromTaskbar`: `App.cs:558-566` and `631-639`.
  - Raw-response diagnostics: `ProviderDefined.cs:578-579` and `646-647`.
  - 401/403 check: `ProviderDefined.cs:596-597` and `655-656`.
  - Token-expiry check: `ProviderDefined.cs:495-496` and `503-504`.
  - `C.Last == null ? null : C.Last.Providers.FirstOrDefault(...)`: `SettingsWindow.cs:255,287,315,596`.
- Long methods to split at their existing commented sections: `Ui.ProviderCard` (688-868, 181 lines), `Ui.LocalCard` (104 lines), `SettingsWindow.Options` (336-402; the key rows 368-396 could become `KeyRows`).
- Not verified: `Sqlite` declares `StdCall` (`ProviderDefined.cs:892-904`) while `HistoryDatabase.cs` declares `Cdecl` for the same `winsqlite3.dll`. It makes no difference on x64; check before touching it.
