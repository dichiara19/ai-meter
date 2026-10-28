# Contributing to AI Meter

Thanks for helping! AI Meter is a small, native Windows 11 app, and contributions of every size are welcome: a bug report, a translation, a new service or a feature.

## Ways to contribute

- **Report a bug** or **ask for a feature**: open an [issue](https://github.com/dichiara19/ai-meter/issues/new/choose) with the matching form.
- **Questions, ideas, your setup**: use [Discussions](https://github.com/dichiara19/ai-meter/discussions).
- **Add a service**: most services are a small JSON file, no code needed. See [docs/PROVIDERS.md](docs/PROVIDERS.md).
- **Translate**: strings live in [`src/StringsTable.cs`](src/StringsTable.cs). Improving an existing language or adding a new one are both welcome.
- **Security issues**: please don't open a public issue, see [SECURITY.md](SECURITY.md).

For anything bigger than a small fix, open an issue first so we can agree on the approach before you write code.

## Build and run

You need Windows 10 or 11; no SDK and no Visual Studio are required.

```powershell
# compile into bin\ with the C# compiler that ships with .NET Framework 4.8
powershell -ExecutionPolicy Bypass -File build.ps1 -NoInstall
```

Run the regression tests with `powershell -NoProfile -Sta -ExecutionPolicy Bypass -File tests/run.ps1`. They compile the application sources in memory, use temporary storage, and contact only local HTTP fixtures. They do not start AI Meter or use your saved accounts. CI runs them before building.

- **Run the build:** start `bin\AIMeter.exe`. If Smart App Control blocks unsigned executables on your PC, run `AIMeter.ps1` from the repository instead (`Start AI Meter.cmd` does the same).
- **Close the installed copy first.** Only one instance runs at a time: exit any installed AI Meter from its menu before starting your build.
- **`--demo`** shows demo data, handy for UI work and screenshots.
- **`tools\render-media.ps1`** regenerates the README images in `docs\media` from the real interface (the GIF needs ffmpeg).
- **`build.ps1 -Package`** also builds the Microsoft Store MSIX (needs the Windows SDK, which GitHub Actions has).

## Guidelines

**Code**
- C# 5, compiled by `csc.exe` of .NET Framework 4.8 with `/langversion:5`. That means no string interpolation, `?.`, `nameof` or expression-bodied members. Pull requests that don't build are caught by CI.
- No NuGet packages and no new dependencies. The app uses WPF and the Windows APIs only; no WebView, no Electron.
- Match the style of the surrounding code. Write comments in English, explaining behavior or constraints that are not obvious from the code. Translate existing comments when revising a file.
- Keep it light. No tight polling loops and no timers under a second. Read files only when they change and large logs only from their tail. Never wake WSL: it is read only when it is already running.

**Strings**
- Write user-facing text in English through `L.T`, `L.F` or `L.N`.
- Add the translations to `src/StringsTable.cs` for the other five languages. If you can't, say so in the pull request and we'll fill them in.

**Privacy rules** (non-negotiable)
- **No telemetry.** No analytics and no update checks.
- **Network only to the service's own usage endpoint**, and only for services the user turned on. Local models use `127.0.0.1` only, with requests that never load a model.
- **Never refresh, store or send another app's tokens** anywhere but to that service. Never read browser cookies.
- **Saved API keys** are encrypted with Windows Data Protection.

**User interface**
- Follow Windows 11 Fluent design, in both themes.
- Attach screenshots in light and dark mode to pull requests that change the UI. `--demo` helps.

## Commits and pull requests

1. Fork the repository and create a branch from `main` (for example `fix/taskbar-dpi` or `feat/gemini-cli`).
2. Keep one topic per pull request.
3. Write commit messages in English with an imperative title: "Add Gemini CLI quota", "Fix pill position with a vertical taskbar".
4. Open the pull request and fill in the template. The CI build must pass.
5. A maintainer reviews it. Pull requests are merged with **squash**, so the pull request title becomes the commit title.

## License

AI Meter is licensed under the [Apache License 2.0](LICENSE). As stated in its section 5, contributions you submit are licensed under the same terms; there is no CLA. The project name and logo are covered by [TRADEMARKS.md](TRADEMARKS.md).
