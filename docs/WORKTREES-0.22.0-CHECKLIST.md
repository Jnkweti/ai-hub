# Per-agent worktrees — 0.22.0 checklist

- [x] Three task-scoped worktrees created from HEAD and reused across phases; layout saved in the ledger.
- [x] Agent CLIs run in their own worktree; assignment explains the arrangement.
- [x] Integration merged in before each turn; agent changes committed and merged into integration after each turn.
- [x] Snapshots, evidence, research and the packet read the integration worktree.
- [x] Conflicts aborted and reported; worktree left clean with its own versions.
- [x] Merge into project and Remove worktrees in the Tasks window.
- [x] Regression cases added; existing suite passes.
- [x] Package built and desktop smoke checks passed.
- [x] Clean checkout built and passed the full suite.
- [x] Installed with the user's approval (September 28, 2026) and reopened as 0.22.0.0; setting off by default.
