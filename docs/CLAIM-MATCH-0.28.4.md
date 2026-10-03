# Check claims match the typed command — 0.28.4

From the [execution-routing run](PILOT-BASELINE-0.28.0.md): asked to run two commands, Codex spent most of a
4½-minute turn re-running them (seven captured commands for two requested) because `complete_work` for a `check`
requires captured evidence whose command equals the claim's `operation` exactly, and what the host records for Codex
on Windows is the full PowerShell invocation — `"…\powershell.exe" -NoProfile -Command 'python -m pytest tests -q'` —
not the command the agent typed and claimed. Codex eventually re-claimed with the wrapper text to make the match.

| Change | Effect | Where |
| --- | --- | --- |
| Wrapper-aware matching | A captured command matches a claim's operation when the whole line equals it, or when the line is a recognized shell wrapper (`powershell`/`pwsh`/`cmd`/`bash`/`sh`/`zsh`, with any flags, then `-Command`, `-c`/`-lc` or `/c`) whose quoted command equals it after unquoting. A wrapper around a different command, or a command that merely mentions the operation, does not match. | `CollaborationStore.CommandMatches`, `CompleteWork` |

## Verification

- Build: zero warnings, zero errors. Full regression suite: **259 tests passed** — one new case covers the
  single-quoted PowerShell wrapper from the live run, a double-quoted wrapper with escaped quotes, a `bash -lc`
  wrapper, a bare command, two non-matches (a wrapper around a longer command; an `echo` that mentions the command),
  and a `check` claim completed from evidence captured through the wrapper. The existing check-reuse case is unchanged.
- Package `artifacts\claim-match-0284-release` (0.28.4.0) passed both desktop smoke checks; installed on October 3,
  2026 after closing the idle 0.28.3 app (`artifacts\claim-match-0284-install-result.json`: 612 files verified, hashes
  match, data unchanged, backups `before-collaboration-20261003-170904.zip` and
  `before-collaboration-data-20261003-170904.zip`); reopened as 0.28.4.0 with 5 rooms and 2 tasks.
- Live: not separately exercised; the next routed run will show whether Codex completes its check claims on the first
  attempt.
