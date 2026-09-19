# Release process

A `vX.Y.Z` tag builds GitKay with NativeAOT on Windows, Linux, and macOS, and publishes a GitHub release with:

- `gitkay-X.Y.Z-setup-x64.exe`: per-user NSIS installer (Start menu shortcut, `gitkay` on PATH, uninstaller;
  `/S` for silent)
- `gitkay-X.Y.Z-win-x64.zip`: portable Windows build
- `gitkay-X.Y.Z-linux-x64.tar.gz`
- `gitkay-X.Y.Z-osx-x64.tar.gz`
- `gitkay-X.Y.Z-osx-arm64.tar.gz`
- `SHA256SUMS.txt`

`VersionPrefix` in `Directory.Build.props` is only the local default; the tag decides the shipped version.

## Releasing

1. Optionally write `dev-docs/releases/X.Y.Z.md`; without it the release notes are generated from commits.
2. Check locally: `dotnet build GitKay.slnx -c Release -m:1 && dotnet test tests/GitKay.Tests -c Release`.
3. Commit and push `main`, then `git tag vX.Y.Z && git push origin vX.Y.Z`.

To retry a failed release, use **Actions → Release → Run workflow** with the version. An existing GitHub release is
kept as-is.

## Elmish.Avalonia.Glue dependency

GitKay references `Elmish.Glue.Core` and `Elmish.Avalonia.Glue` from NuGet at `ElmishGlueVersion`
(`Directory.Build.props`). Locally, if a checkout exists at `../../../Elmish.Avalonia.Glue/main` (relative to the
repo root) it builds against that source instead. CI always uses the package. Override with
`-p:UseLocalGlue=false` to test against the published package, or `-p:GlueSourceRoot=<path>/` for another checkout.

When a GitKay change needs a Glue change: release Glue first, then bump `ElmishGlueVersion`.

## One-time setup: winget

winget packages live in [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs). The first version must be
submitted by hand; after that the `winget` job opens the update PR for each release.

1. Publish the first GitHub release (above).
2. Fork `microsoft/winget-pkgs` to your account (the automation pushes branches to this fork).
3. Submit the first manifest with [wingetcreate](https://github.com/microsoft/winget-create) on Windows:

   ```powershell
   winget install Microsoft.WingetCreate
   wingetcreate new https://github.com/adz/gitkay/releases/download/v0.1.0/gitkay-0.1.0-setup-x64.exe
   ```

   Use package identifier `AdamDavies.GitKay` (it must match `identifier` in `release.yml`), installer type
   `nsis`, scope `user`, license `Apache-2.0`. Let it submit the PR. Validation bots run on it, then a
   moderator merges it, usually within a few days.
4. Create a **classic** personal access token with `public_repo` scope
   (fine-grained tokens can't open PRs against repos you don't own) and add it as repo secret `WINGET_TOKEN`.
5. Add repo variable `WINGET_ENABLED` = `true`.

From then on every non-prerelease tag submits `AdamDavies.GitKay` at the new version. The token expires, so renew
it when winget submissions start failing.

The installer is unsigned, so Windows SmartScreen warns on first run. winget accepts unsigned installers.
Azure Trusted Signing can be added to the Windows build job later.
