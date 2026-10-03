# Question resolution — 0.28.0 checklist

- [x] Checkout inspected; the uncommitted activity-snapshot, vision and plan edits were preserved and not staged.
- [x] Current coordinator behavior mapped from source and tests; the open challenge loop named as the gap.
- [x] An answered `question` returns to the asker as a resolution turn carrying the answer; the decision is recorded with links to both messages.
- [x] Auto-collaboration off pauses; unavailable askers are skipped; round cap and repair budget still apply; schema and stored formats unchanged.
- [x] Codex default-model fallback retries on a fresh thread (resume kept the rejected model on CLI 0.159.1).
- [x] Regression cases added; full suite passes (255).
- [x] Live check with both real CLIs passes (`--challenge-live`), evidence under `artifacts\challenge-live-028`.
- [x] Package built from `572377d`, desktop smoke checks passed, installed on the user's go-ahead ("ok get to work", October 3, 2026) and reopened as 0.28.0.0.
- [ ] Unprescribed pilot task and solo starting point (Phase 0/2).
