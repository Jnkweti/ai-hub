# Hardening — 0.15.0 checklist

- [x] Evidence capture off the provider reader thread, drained before commit and abort.
- [x] Stale review set aside as Interrupted with a repairable validation error; replacement terminal accepted.
- [x] Claim released before the final task write; live entry ends on failure.
- [x] Repair budget spent only by invalid structured submissions.
- [x] Pipe listener survives non-cancellation errors.
- [x] Manifest pruning: prompt text retained for the newest 24, oldest earlier-phase manifests evicted at 128.
- [x] Incremental transcript import with inline-to-source migration preserved.
- [x] Bounded, fail-safe window close.
- [x] Regression cases added; existing suite passes.
- [x] Package built and desktop smoke checks passed.
- [ ] Live provider check (deferred: Codex quota exhausted until October 3, 2026; changes are host-side).
- [ ] Installed with the user's approval.
