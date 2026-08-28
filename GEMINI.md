# PowerQuota Development Guidelines

## Store Packaging & Environment Variables
- Store identity and packaging values (`PublisherDisplayName`, `PackageIdentityPublisher`, `PackageIdentityName`) are maintained in the Windows User environment registry and automatically resolved by [`build-msix.ps1`](file:///c:/Users/ericj/source/PowerQuota/build-msix.ps1) across process and user scopes.
- See detailed documentation in [`.agents/rules/store-packaging.md`](file:///c:/Users/ericj/source/PowerQuota/.agents/rules/store-packaging.md).

## Versioning & Releases
- Keep versions synchronized across [`Directory.Build.props`](file:///c:/Users/ericj/source/PowerQuota/Directory.Build.props) and [`Package.appxmanifest`](file:///c:/Users/ericj/source/PowerQuota/src/PowerQuota.CommandPalette/Package.appxmanifest).
- Releases are triggered by Git tags matching `v*`.

## Pull Requests & GitHub CLI
- When creating or editing PRs with `gh pr create` / `gh pr edit`, always supply the body via `--body-file <path>` (or verbatim single-quoted here-strings `@' ... '@`) to prevent PowerShell from interpreting markdown backticks as escape sequences (e.g. `` `a `` expanding to bell character `\a` or `` `r `` to carriage return).
