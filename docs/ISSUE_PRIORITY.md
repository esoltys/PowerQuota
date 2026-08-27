# Issue Priority & Status

Priority (P1–P4) and Status (Todo/In Progress/Done/Parked) are tracked exclusively as fields on
the "PowerQuota" GitHub Project (`gh project` number `4`, owner `esoltys`) — never as
labels. Both are readable and settable directly through the `gh project` CLI; there's no need to
ask the user to update them by hand or fall back to a label as a substitute.

## Adding a new issue to the board

A freshly created issue isn't a Project item yet, so it has no Priority/Status to read or set
until it's added:

```bash
gh project item-add 4 --owner esoltys --url <issue-url>
```

## Reading current values

```bash
gh project item-list 4 --owner esoltys --format json
```

Each item in the result includes `priority` and `status` directly, plus `content.number` so you
can match it to a specific issue.

## Setting a value

Find the item's `id` from the `item-list` output above (matching on `content.number`), then:

```bash
gh project item-edit --project-id PVT_kwHOAAE3ZM4BhmFH --id <item-id> \
  --field-id <field-id> --single-select-option-id <option-id>
```

**Priority** — field id `PVTSSF_lAHOAAE3ZM4BhmFHzhghTJ8`:

| Option | id |
| --- | --- |
| P1 | `b78fc3e3` |
| P2 | `c2c7cd32` |
| P3 | `739b6916` |
| P4 | `af8bd358` |

**Status** — field id `PVTSSF_lAHOAAE3ZM4BhmFHzhghQcI`:

| Option | id |
| --- | --- |
| In Progress | `628fb77d` |
| Todo | `21989f11` |
| Done | `039def17` |
| Parked | `bbe8e5ec` |

If the Project's fields are ever recreated, these IDs will change — re-run `gh project field-list 4 --owner esoltys --format json` and update this table.

## Priority scheme

Every bug/feature issue gets a Priority when it's created, regardless of milestone — set Status
to "Todo" and assign a Priority using this scheme:

### Bugs
- **P1** — Critical system down with no workaround (stops the extension/providers from functioning; if the user can still use the app or other providers then it's not a P1).
- **P2** — Severe degradation, workaround exists (e.g. key provider broken or bad parsing fallback).
- **P3** — Limited impact, single provider or non-critical edge case affected.
- **P4** — Inconvenience, cosmetic/formatting.

### Features
- **P1** — Foundational work that other features or providers depend on.
- **P2** — High-impact providers (e.g. Copilot, OpenAI/Codex, Cursor, Claude) or core UX features in Command Palette & Dock.
- **P3** — Secondary providers, lower-frequency, or power-user features.
- **P4** — Speculative, experimental, or low-demand integrations.

Don't default new issues to P2/P3 — assign a priority using the criteria above.
