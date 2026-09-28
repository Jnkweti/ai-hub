# Voluntary follow-ups and local diagnostics — 0.12.0 checklist

Scope: finish the work Codex handed over in [CLAUDE-HANDOFF-2026-09-27.md](CLAUDE-HANDOFF-2026-09-27.md)
to a releasable state. Items are checked only with recorded evidence in
[LOCAL-DIAGNOSTICS-0.12.0.md](LOCAL-DIAGNOSTICS-0.12.0.md).

## Handoff items

- [x] Review the follow-up scheduler: explicit peer requests keep their routing and framing; voluntary follow-ups are framed as optional; stop during a follow-up records the assignment as interrupted and releases ownership. Regression cases added.
- [x] Exercise the diagnostics UI in an isolated profile: report, prepare draft, read-only audit room, restart, close (`tests\Local-Diagnostics-Smoke.ps1`). Not covered: sending the review to real providers and the export file dialog.
- [x] Harden the audit lifecycle: disposed or deleted rooms are forgotten; pending waits survive a collection toggle; clearing works while collection is off.
- [x] Per-record application version on findings; findings from different versions are not merged; the report shows the version.
- [x] Wire unhandled-error hooks with an immediate bounded flush that does not suppress the failure.
- [x] Decide on richer fingerprints: not in this release; findings remain category, count, timing and references (documented as a limit).
- [x] Validate the final package and update version and release docs. Installation waits for the user's approval after confirming the production app is idle.

## Found during the handover

- [x] Fix the bridge shutdown crash that made "revokes on stop" timing-dependent; add stderr capture and exit timing to the test.
- [x] Add `--only` and `--repeat` to the test runner.
- [x] Move the diagnostics action to an icon button so the compact rail keeps its room list.

## Release

- [x] Version 0.12.0 in the desktop project and both plugin manifests.
- [x] README, ARCHITECTURE and release documentation updated.
- [x] Full regression suite passed: 199 tests.
- [x] Desktop smoke scripts passed against the packaged app: local diagnostics and conversation management.
- [x] Schema fixtures and the Claude plugin manifest validated.
- [x] Package built to `artifacts\diagnostics-012-release`.
- [ ] App and data backed up; installed; reopened idle (requires the user's approval).
