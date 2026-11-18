# Releasing EliteLevel Discfolio

The tag workflow builds the app, packs it with [Velopack](https://github.com/velopack/velopack), and publishes a GitHub release. Do the steps below on `main` after the version you want is merged.

1. Set `<Version>` in `Directory.Build.props` to `X.Y.Z`.
2. Add a `CHANGELOG.md` section headed `## X.Y.Z`. The release notes are that section.
3. Commit those changes.
4. Tag `vX.Y.Z` and push the tag.

```text
git tag vX.Y.Z
git push origin vX.Y.Z
```

Pushing the tag starts `.github/workflows/release.yml`. It tests, publishes a self-contained build, runs `vpk pack`, writes `SHA256SUMS.txt`, and creates the GitHub release with `vpk upload github` using `GITHUB_TOKEN`.

A prerelease tag has a hyphen after the version, for example `v0.4.0-beta.1`. The changelog heading must be `## 0.4.0-beta.1`. That tag is packed on the prerelease channel and uploaded as a GitHub pre-release. Any other `v*` tag is stable.

## What the Windows release contains

`vpk pack` writes:

- the setup program
- the portable zip
- the full nupkg
- a delta nupkg when a previous release of that channel is already published

The setup program installs per user to `%LocalAppData%\Discfolio`. It adds Start Menu and Desktop shortcuts, uses the Discfolio icon, registers in Installed apps, and uninstalls cleanly. The pack id is `Discfolio`.

The Windows channel is `win-x64-stable` or `win-x64-prerelease`. Linux uses `linux-x64-stable` or `linux-x64-prerelease`. macOS uses `osx-arm64-stable` or `osx-arm64-prerelease`. Windows is required and is published first. The Linux pack is best-effort and does not fail the workflow. The macOS pack is required. If it fails, the workflow fails. The Windows release is already published by then.

The macOS job runs on `macos-latest`. That image's `/bin/bash` is bash 3.2, and the script uses `set -u`. On bash 3.2, expanding an empty array (`"${name[@]}"`) is an unbound variable. A stable tag left `pre` empty, so the job exited with `pre[@]: unbound variable` before `vpk` ran. `continue-on-error` still marked the release workflow successful, and v0.4.0 shipped with no macOS files. The script now keeps each `vpk` argument list non-empty and appends `--pre` only for a prerelease tag.

`vpk` 1.2.161 builds the `.app` bundle and accepts only an `.icns` icon. The file is `src/EliteLevel.Disc.App/Assets/icon.icns`, the 128×128 `icon.png` stored as an `ic07` image. It is not an Avalonia resource. A PNG is rejected. Before upload, the job checks for the full nupkg, `*-Portable.zip`, and `*-Setup.pkg`.

The hosted runner has no Apple Developer identity, so the pkg and the app are unsigned and not notarized. Gatekeeper asks before the app opens. Signing needs a Developer ID and a notary profile, which this workflow does not have.

CI on pull requests also runs `vpk pack` for Windows and does not upload. The version packed there is the `<Version>` in `Directory.Build.props`. The tag workflow packs the version from the tag.

## Updates

The app reads the public GitHub releases for `EliteLevelSoftware/EliteLevelDiscfolio`. Users do not need a token.

A private feed is an override. Set the update source in Settings, and provide a token with the `DISCFOLIO_UPDATE_TOKEN` environment variable or with `UpdateToken` in the user settings file. Neither value is part of the build. A public repository, or a public releases repository, does not need a token.
