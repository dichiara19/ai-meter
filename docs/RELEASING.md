# Releasing

AI Meter follows [Semantic Versioning](https://semver.org) and GitHub's pre-release flag. Every release is built by the [workflow](../.github/workflows/build.yml) from a pushed tag; nothing is built or uploaded by hand.

| Tag | GitHub release | Update notice in the zip and script builds | Microsoft Store |
|---|---|---|---|
| `vX.Y.Z` | **Latest** | everyone with the check on | submitted (when configured) |
| `vX.Y.Z-rc.N` | pre-release, never Latest | only with *Include pre-releases* | never |
| `vX.Y.Z-beta.N` | pre-release, never Latest | only with *Include pre-releases* | never |

The update check reads GitHub's `releases/latest`, which excludes pre-releases and drafts, so users only hear about stable versions unless they opt in. Any other tag shape (`v1.2`, `v1.2.3-alpha.1`, `v1.2.3-rc.01`) fails the workflow before anything is built.

## Cut a beta or release candidate

1. In `src/AssemblyInfo.cs` set the version you are working towards, with the suffix in the informational version only:

   ```csharp
   [assembly: AssemblyVersion("0.9.0.0")]
   [assembly: AssemblyFileVersion("0.9.0.0")]
   [assembly: AssemblyInformationalVersion("0.9.0-beta.1")]
   ```

2. Update `RELEASE_NOTES.md` (it becomes the release body) and commit.
3. Tag and push:

   ```bash
   git tag v0.9.0-beta.1
   git push origin v0.9.0-beta.1
   ```

The release appears under *Releases* marked **Pre-release**. Increment `N` for each new build (`beta.2`, …, then `rc.1`, …): pre-releases sort `beta.2 < beta.11 < rc.1 < 0.9.0`.

## Cut a stable release

1. In `src/AssemblyInfo.cs` remove the suffix: all three versions say `0.9.0` (`0.9.0.0` for the first two).
2. Update `RELEASE_NOTES.md` and the *What changed* section of the README, commit.
3. Tag and push `v0.9.0`.

The release becomes **Latest**. Within a day, zip and script users with the update check on see a notice linking to it, and beta users see that the stable version supersedes their beta.

## What the workflow checks

- The tag matches `vX.Y.Z`, `vX.Y.Z-beta.N` or `vX.Y.Z-rc.N`.
- `AssemblyVersion` and `AssemblyFileVersion` are `X.Y.Z.0`, and `AssemblyInformationalVersion` is the tag without the `v`. The app reports the informational version and compares it with GitHub's, so a beta binary can never claim to be the stable release. The MSIX version is `X.Y.Z.0`.

## When the Store gets a submission

Only stable tags reach the Microsoft Store. Once the Store job is configured (see [STORE.md](STORE.md#automatic-submission)) a stable tag publishes the GitHub release first, then submits `AIMeter.msix` to Partner Center for certification. Without that configuration, download the **AIMeter-msix** artifact of the stable tag's run and upload it by hand. Never upload the MSIX from a pre-release run: its package version is the same `X.Y.Z.0` as the stable release, and the Store rejects a version it has already received.

## If something goes wrong

- A tag failed the version check: delete it (`git push --delete origin vX.Y.Z` and `git tag -d vX.Y.Z`), fix `src/AssemblyInfo.cs`, tag again.
- A stable release turned out broken: publish a fixed `vX.Y.Z+1`. Avoid deleting a stable release, since users who already got the notice would land on a missing page.
