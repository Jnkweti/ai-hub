# Roadmap status after the first harness evaluation — 0.28.5

A documentation release. On the user's instruction, Codex's uncommitted work was committed as-is (the Open full log
snapshot, `PROJECT-VISION.md`, the learning plan, the TODO roadmap, the handoff), and the roadmap documents were then
brought up to date with what 0.28.0 to 0.28.4 shipped.

## What changed

| Change | Where |
| --- | --- |
| Phase 0 (all four items), Phase 1 (all eight) and Phase 2 items 1-5 ticked, each with the release or document that evidences it. Phase 2's remaining items are named in `docs/TODO.md`: the answers-then-synthesis comparison, task-fitted contribution selection, selectable strategies, and a task hard enough to produce a wrong first answer. | `docs/EMERGENCE-AND-LEARNING-PLAN.md`, `docs/TODO.md` |
| README version lines for 0.27.0 and 0.28.0-0.28.5, which were owed while README carried uncommitted edits. | `README.md` |

## Where the harness stands

- The handoff's definition of done is met and demonstrated with real providers: a contribution, a focused peer
  question, an evidence-backed answer, and a recorded decision, each linked in the ledger (sixth pilot run,
  `PILOT-BASELINE-0.28.0.md`). Fixtures cover the mechanism's failure paths; 259 tests pass.
- Measured so far: on three small planted-bug tasks every arm was correct; collaboration added verification and one
  or two substantive points per run at 1.4-2.2× Codex-alone elapsed time and about double the spend. One run is a
  demonstration, not a measurement of improved outcomes.
- Known gaps: an endorsement reaction turn slips past the repeat check; a `blocked` answer pauses before the asker
  acts; the read-only Codex sandbox cannot run pytest; the challenge path has fired on its own only when the asker
  needed execution, never from disagreement, because no first answer was wrong.

## Verification

- Build: zero warnings, zero errors; full regression suite: 259 tests passed (no code change in this release beyond
  the version).
- Package `artifacts\roadmap-0285-release` (0.28.5.0) built with `Build.ps1 -Test` and passed both desktop smoke
  checks; installed on October 3, 2026 after closing the idle 0.28.4 app (`artifacts\roadmap-0285-install-result.json`:
  612 files verified, hashes match, data unchanged, backups `before-collaboration-20261003-173415.zip` and
  `before-collaboration-data-20261003-173415.zip`); reopened as 0.28.5.0 with 5 rooms and 2 tasks.
