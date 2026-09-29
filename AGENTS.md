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

## Architecture

Three projects under `src/`:

- **PowerQuota.Core** — headless engine, no UI/WinRT dependencies.
  - `Providers/IProviderAdapter.cs` is the contract every AI provider
    implements: `FetchAsync(AccountConfig, WindowsCredentialVault,
    HttpClient, ct)` returns a `UsageSnapshot`, plus
    `GetSystemActiveAccountIdAsync` for auto-detecting which
    locally-logged-in account is active. Add a new provider by
    implementing this interface (see `ClaudeProvider.cs`,
    `CodexProvider.cs`, etc.) and registering it wherever adapters are
    enumerated (`Engine/QuotaRefreshService.cs`).
  - `Engine/QuotaRefreshService.cs` polls all configured provider
    adapters on a schedule with exponential backoff on rate-limit
    responses.
  - `Storage/WindowsCredentialVault.cs` encrypts all stored
    tokens/credentials via Windows DPAPI (`ProtectedData`,
    `CurrentUser` scope) at `$env:LOCALAPPDATA\PowerQuota\vault.dat`.
    Never store credentials in plaintext.
  - `Storage/HostCliScanner.cs` does read-only auto-discovery of
    existing CLI credentials (e.g. `~/.claude/.credentials.json`,
    `~/.codex/auth.json`, Cursor's `state.vscdb`). Must stay strictly
    read-only — never mutate another tool's session/credential files.
  - `Storage/ConfigStorage.cs` / `Storage/Config.cs` hold non-secret
    app configuration. `Models/UsageModels.cs` defines
    `UsageSnapshot`/`UsageWindow`/`AppState`; `Models/ProviderId.cs`
    enumerates supported providers.

- **PowerQuota.CommandPalette** — the WinRT COM server extension that
  plugs into PowerToys Command Palette/Dock (`Program.cs` is the
  out-of-process COM entrypoint; `Package.appxmanifest` declares the
  `com.microsoft.commandpalette` AppExtension).
  `Providers/PowerQuotaCommandProvider.cs` implements
  `ICommandProvider4` and `GetDockBands` — the integration surface
  PowerToys calls into. `Pages/` holds the Command Palette UI
  (`OverviewListPage`, `ProviderDetailsPage`, `AddAccountFormPage`,
  `SettingsFormPage`). Keep provider-specific parsing/auth logic in
  Core, not here.

- **PowerQuota.Core.Tests** — xUnit tests for providers, storage, and
  the refresh engine.

## Privacy invariant

Zero telemetry, no third-party network calls — every request goes
directly from the user's machine to the official provider API
(`api.anthropic.com`, `chatgpt.com`, `cursor.com`, `github.com`, etc.).
Don't introduce any analytics/telemetry dependency or route data
through an intermediate server.

## Issue priority & status

Priority and Status are tracked as fields on the GitHub Project, not as labels or
in-body text — see `docs/ISSUE_PRIORITY.md` for the scheme and the `gh project` commands.

## Pull requests & GitHub CLI

When creating or editing PRs with `gh pr create` / `gh pr edit` from
PowerShell, pass the body via `--body-file <path>` (or a verbatim
single-quoted here-string `@'...'@`) rather than `--body "..."` —
PowerShell double-quoted strings interpret backticks as escape
sequences (e.g. `` `a `` becomes a bell character, `` `r `` a carriage
return), which can silently corrupt markdown in the body.
