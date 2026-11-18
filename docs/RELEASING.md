# Releasing EliteLevel Discfolio

The tag workflow builds the app, packs it with [Velopack](https://github.com/velopack/velopack), and publishes a GitHub release. Do the steps below on `main` after the version you want is merged.

1. Set `<Version>` in `Directory.Build.props` to `X.Y.Z`.
2. Add a `CHANGELOG.md` section headed `## X.Y.Z`. That section is the changelog in the GitHub release description, and it is the notes stored in the nupkg.
3. Commit those changes.
4. Tag `vX.Y.Z` and push the tag.

```text
git tag vX.Y.Z
git push origin vX.Y.Z
```

Pushing the tag starts `.github/workflows/release.yml`. It tests, publishes a self-contained build, runs `vpk pack`, and creates the GitHub release with `vpk upload github` using `GITHUB_TOKEN`. After the packs have uploaded, the workflow writes `SHA256SUMS.txt` from the digests of the files on that release and attaches it.

A prerelease tag has a hyphen after the version, for example `v0.4.0-beta.1`. The changelog heading must be `## 0.4.0-beta.1`. That tag is packed on the prerelease channel and uploaded as a GitHub pre-release. Any other `v*` tag is stable.

## What a release contains

`vpk pack` runs with `--delta none`. Delta packages are not built, and the workflow does not download the previous release. On Windows and macOS, `--noPortable` skips the portable zip. The Linux pack command in vpk 1.2.161 does not accept `--noPortable`, so the Linux job omits that flag. Linux still builds one AppImage, which is the full package, and does not produce a portable zip. The Linux upload is that renamed AppImage, the full nupkg, and `releases.linux-x64-<track>.json`.

The files a person downloads are renamed after packing:

- `Discfolio-<version>-Windows-Setup.exe`
- `Discfolio-<version>-macOS-AppleSilicon-Setup.pkg`
- `Discfolio-<version>-Linux-x64.AppImage`

`<version>` is the tag without the leading `v`, for example `0.6.2`. The setup program installs per user to `%LocalAppData%\Discfolio`. It adds Start Menu and Desktop shortcuts, uses the Discfolio icon, registers in Installed apps, and uninstalls cleanly. The pack id is `Discfolio`. The macOS pkg is unsigned. The Linux AppImage is attached when that pack succeeds.

`vpk upload` sends the paths listed in `assets.<channel>.json`. The rename step writes the new download name into that file. The full nupkg and the releases index stay as `vpk` named them:

- `Discfolio-<version>-win-x64-<track>-full.nupkg`
- `Discfolio-<version>-linux-x64-<track>-full.nupkg`
- `Discfolio-<version>-osx-arm64-<track>-full.nupkg`
- `releases.win-x64-<track>.json`
- `releases.linux-x64-<track>.json`
- `releases.osx-arm64-<track>.json`

`<track>` is `stable` or `prerelease`.

The in-app updater uses `GithubSource` against this public repository. It loads `releases.<channel>.json` from each recent GitHub release, then downloads the `FileName` recorded in that file. That name is the full nupkg. The setup program, the pkg, and the AppImage are not in the feed, so renaming them leaves updates working.

On Linux, applying an update extracts the `.AppImage` stored inside the nupkg. That entry is named `Discfolio.AppImage`. The updater moves it onto the AppImage that is running, which is the path in `$APPIMAGE`. It does not look up the AppImage by its name on the GitHub release. An AppImage downloaded under the new name still updates.

`SHA256SUMS.txt` lists every other file on the release, under the name that was uploaded. It is written from the GitHub asset digests after the packs upload, so a renamed installer is hashed under the new name. The checksum file is attached after that list is written, and it does not include its own digest.

The Windows channel is `win-x64-stable` or `win-x64-prerelease`. Linux uses `linux-x64-stable` or `linux-x64-prerelease`. macOS uses `osx-arm64-stable` or `osx-arm64-prerelease`. Windows is required and is published first. The Linux pack is best-effort and does not fail the workflow. The macOS pack is required. If it fails, the workflow fails. The Windows release is already published by then.

The release description starts with a Download table for the three installers, then a section named "Auto-updater files (you don't need these)", then the changelog notes for that version. The notes stored in the nupkg stay the changelog text, which is what the update dialog shows.

The macOS job runs on `macos-latest`. That image's `/bin/bash` is bash 3.2, and the script uses `set -u`. On bash 3.2, expanding an empty array (`"${name[@]}"`) is an unbound variable. A stable tag left `pre` empty, so the job exited with `pre[@]: unbound variable` before `vpk` ran. `continue-on-error` still marked the release workflow successful, and v0.4.0 shipped with no macOS files. The script now keeps each `vpk` argument list non-empty and appends `--pre` only for a prerelease tag.

`vpk` 1.2.161 builds the `.app` bundle and accepts only an `.icns` icon. The file is `src/EliteLevel.Disc.App/Assets/icon.icns`, the same full set as `assets/branding/discfolio.icns` (16 through 1024). It is not an Avalonia resource. A PNG is rejected. Before upload, the job checks for `Discfolio-<version>-osx-arm64-<track>-full.nupkg`, `releases.osx-arm64-<track>.json`, and the setup pkg, then renames the pkg. A portable zip or a delta package fails the job.

The hosted runner has no Apple Developer identity, so the pkg and the app are unsigned and not notarized. Gatekeeper asks before the app opens. Signing needs a Developer ID and a notary profile, which this workflow does not have.

CI on pull requests also runs `vpk pack` for Windows and for Linux, and does not upload. Windows uses `--delta none` and `--noPortable`. Linux uses `--delta none` and does not pass `--noPortable`. Each job renames the download the same way as the tag workflow and checks that the full nupkg and the releases index kept their names. The macOS pack dry run starts only from a workflow dispatch, so it does not block a pull request. The version packed there is the `<Version>` in `Directory.Build.props`. The tag workflow packs the version from the tag.

## Updates

The app reads the public GitHub releases for `EliteLevelSoftware/EliteLevelDiscfolio`. Users do not need a token.

A private feed is an override. Set the update source in Settings, and provide a token with the `DISCFOLIO_UPDATE_TOKEN` environment variable or with `UpdateToken` in the user settings file. Neither value is part of the build. A public repository, or a public releases repository, does not need a token.
