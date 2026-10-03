# Explicit feedback — 0.29.0 checklist

- [x] Feedback on a message or a task: kind, optional dimensions, explanation, developer-chosen scope.
- [x] Linked by message, dispatch, ledger-message and task IDs, with outcome, app version and strategy fingerprint; judged text hashed, not copied.
- [x] Local store: versioned, validated, repaired on load, capped; conversation deletion removes its feedback; never read by agent paths.
- [x] Inspect, edit, delete and export (All feedback window from the Tasks window; conversation export and review packet sections).
- [x] Regression cases added; full suite passes (264).
- [x] Package built, desktop smoke checks (including the new `Feedback-Smoke.ps1`) passed, installed on October 3, 2026 and reopened as 0.29.0.0.
