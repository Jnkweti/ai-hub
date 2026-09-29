# Addressed messages and session carry — 0.26.0 checklist

- [x] A message naming one agent dispatches only that agent; no preparation; peer still reached when the reply asks it.
- [x] Later phases resume native sessions with delta prompts; cursor records task, stream position and input bytes.
- [x] Carry limit and desktop resets start fresh sessions; unmatched sessions fall back to the full core.
- [x] Regression cases added; existing suite passes.
- [x] Live checks with the real CLIs (routing continuation, shared conversation) pass with the new expectations.
- [x] Read-only Claude Code sessions are told what plan mode means here, after two live failures.
- [x] Package built and desktop smoke checks passed.
- [ ] Clean checkout built and passed the full suite.
- [ ] Installed with the user's approval and reopened as 0.26.0.0.
