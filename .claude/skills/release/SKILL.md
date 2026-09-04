---
name: release
description: Cut a PowerQuota release — bump version files, write release notes, tag, and build/submit the Store package
---

Cut release $ARGUMENTS.

[AGENTS.md](../../../AGENTS.md) is the source of truth for versioning and release mechanics —
re-read its "Versioning on release" section before starting. Two files carry the version and must
move together in the same commit, or the built binary reports a stale version (this has happened
before: `Directory.Build.props` sat at `1.4.0` through the v1.5.0 and v1.6.0 releases while the
appx manifest was correctly bumped):

- `Directory.Build.props` — `Version` (`X.Y.Z`), `AssemblyVersion`/`FileVersion` (`X.Y.Z.0`)
- `src/PowerQuota.CommandPalette/Package.appxmanifest` — `Version=` attribute on `<Identity>`
  (`X.Y.Z.0`)

Work through in order:

1. **Preflight**: `git status --porcelain` — confirm the working tree is clean. `git fetch origin`
   and `git log <last-tag>..origin/main --oneline` to see what's landing since the last tag;
   confirm every user-facing fix has a linked, `bug`-labeled issue (file one retroactively and
   link the PR if it's missing).
2. **Branch**: create `claude/release-X.Y.Z` off latest `origin/main` (matches past release PRs,
   e.g. `claude/release-1-6-0`).
3. **Bump the version**: update `Directory.Build.props` and `Package.appxmanifest` together, then
   `grep -rn "<old version>"` across the repo (docs, README, workflow files) to catch anything
   else still referencing the old string.
4. **Release notes**: write `docs/release_notes/vX.Y.Z.md` following the existing format — title,
   a short "Summary", then a "Key Features & Improvements" section with bullets linking issue
   numbers (see `docs/release_notes/v1.6.0.md` for the pattern).
5. **Verify**: `dotnet build -c Release` and `dotnet test -c Release` from the repo root.
6. **Commit & PR**: commit as `release: bump to vX.Y.Z and add release notes`, push the branch,
   and open a PR into `main` with the repo PR template (`gh pr create`). Wait for the user to
   review and merge it — never merge it yourself.
7. **Tag**: after the PR merges, `git checkout main && git pull`, then tag `main`'s tip and push
   the tag — confirm with the user first, since pushing the tag triggers a public release:
   ```bash
   git tag -a vX.Y.Z -m "PowerQuota vX.Y.Z"
   git push origin vX.Y.Z
   ```
   This triggers [`.github/workflows/build-and-release.yml`](../../../.github/workflows/build-and-release.yml)
   (build, test, publish win-x64, zip, GitHub release with auto-generated notes).
8. **Watch the build**: `gh run watch <run-id> --exit-status`.
9. **Post-build**:
   - Edit the GitHub release body, replacing the auto-generated notes with the content of
     `docs/release_notes/vX.Y.Z.md`.
   - Microsoft Store submission is a separate, manual step — see
     [`.agents/rules/store-packaging.md`](../../../.agents/rules/store-packaging.md) for the
     environment variables and `build-msix.ps1 -ForStore -Bundle -Version X.Y.Z` invocation. Only
     do this if the user asks for it.
   - Close/link the GitHub issues resolved by this release and set their Project board Status to
     "Done" (see [`docs/ISSUE_PRIORITY.md`](../../../docs/ISSUE_PRIORITY.md)).

Before calling the release done, confirm: the release workflow is green, the pushed tag matches
`Directory.Build.props`/`Package.appxmanifest`, and the GitHub release has the
`PowerQuota-win-x64.zip` artifact attached.
