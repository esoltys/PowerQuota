# Agent notes for PowerQuota

## Build & test

- Build: `dotnet build -c Release`
- Test: `dotnet test -c Release`
- Solution file: `PowerQuota.sln`; the extension project is
  `src/PowerQuota.CommandPalette/PowerQuota.CommandPalette.csproj`.

## Versioning on release

Two files carry the version number and must be bumped together, or the
built binaries report a stale version even after a release:

- `Directory.Build.props` — `Version`, `AssemblyVersion`, `FileVersion`
- `src/PowerQuota.CommandPalette/Package.appxmanifest` — `Version=` attribute

These have drifted before: `Directory.Build.props` was left at `1.4.0`
through the v1.5.0 and v1.6.0 releases while the appx manifest was
correctly bumped, so any package manager reading the built assembly's
file version showed 1.4.0 instead of the actual 1.6.0.

When bumping the version, update both files in the same commit, and
grep for the old version string across the repo to catch anything else
that references it (e.g. `docs/release_notes/`).

Releases are triggered by pushing a Git tag matching `v*`, which runs
`.github/workflows/build-and-release.yml` (build, test, publish
win-x64, zip, GitHub release). Store packaging (MSIX) is a separate,
manual flow — see `.agents/rules/store-packaging.md` for the
environment variables and steps involved.

## Pull requests & GitHub CLI

When creating or editing PRs with `gh pr create` / `gh pr edit` from
PowerShell, pass the body via `--body-file <path>` (or a verbatim
single-quoted here-string `@'...'@`) rather than `--body "..."` —
PowerShell double-quoted strings interpret backticks as escape
sequences (e.g. `` `a `` becomes a bell character, `` `r `` a carriage
return), which can silently corrupt markdown in the body.
