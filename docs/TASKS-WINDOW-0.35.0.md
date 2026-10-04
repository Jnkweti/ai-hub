# Tasks window regrouped — 0.35.0

The Tasks window's single row of buttons had grown to fifteen as 0.29.0 to 0.34.0 added feedback, preferences, the
strategy report and evaluation tasks. On the developer's go-ahead, the buttons are now three labeled rows, with Close
on its own below them. Every automation id is unchanged, so the existing checks and any scripts keep working.

| Row | Buttons |
| --- | --- |
| This task | Save note · Use this task · New task · Open conversation · Stop task · Mark as evaluation task |
| Collaboration | Check evidence · Review packet · Merge into project · Remove worktrees · Shared context |
| Feedback & learning | Feedback · All feedback · Preferences · Strategy report |

The window opens at 860 × 760 (minimum 680 × 620) to fit the rows without wrapping at the default size; the rows
wrap when narrower.

## Verification

- Build: zero warnings, zero errors; no change to the unit suite (277 tests).
- UI checks, run once with the developer's permission against the package: `Feedback-Smoke.ps1` passed (it opens the
  Tasks window and uses All feedback, Preferences and Close in the new rows, and asserts the app never takes focus);
  `Conversation-Management-Smoke.ps1` passed (compact layout). Two others failed for reasons unrelated to this
  change and were not retried: `Shared-Context-Smoke.ps1` requires a `-NativeResultDirectory` from a live check that
  was not supplied; `Task-Memory-Smoke.ps1` reads message transcripts from `rooms.json`, which has held only headers
  since 0.23.0, so its progress-answer check cannot pass, and it also calls `ShowWindow(restore)` on the app, which
  brings the window to the front regardless of the no-activation switch — the developer saw that and asked for the
  checks to stop. Both scripts need updating before they are run again.
- Installed on October 3, 2026 as 0.35.0.0 (`artifacts\tasks-window-0350-install-result.json`), app left closed.
