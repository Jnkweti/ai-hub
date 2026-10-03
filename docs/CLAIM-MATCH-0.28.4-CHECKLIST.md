# Check claims match the typed command — 0.28.4 checklist

- [x] A check claim's operation matches the command inside a recognized shell wrapper, not only the whole captured line.
- [x] Non-matches stay rejected (different command in the wrapper; a mention of the command).
- [x] Regression cases added; full suite passes (259).
- [x] Package built, smoke checks passed, installed on October 3, 2026 and reopened as 0.28.4.0.
