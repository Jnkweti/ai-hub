# Bounded recovery loops — 0.21.0

The second item of batch 3 in `docs/TODO.md`, from claude_codex_bridge: a provider process that dies in the middle of a
turn is restarted with backoff and the turn continues, instead of the whole run failing on the first crash.

## How it works

- `JsonProcess` reports a process that closed its output as `ProviderProcessException`, a distinct kind of I/O
  failure. Nothing else (frame limits, sign-in errors, usage limits) is treated as a crash.
- In the phase loop, when a turn ends with that failure the coordinator waits the next `RecoveryBackoff` interval
  (30 s, 60 s, 120 s, 5 min, 10 min, 30 min), records audit code `ProviderRestart`, tells the user "…'s process ended
  mid-turn. Restarting it in …, attempt n of 6; the same native session resumes", and asks the same turn again. The
  resident client restarts the CLI and resumes its native session (`--resume` for Claude Code, `thread/resume` for
  Codex), so the agent continues where it was; the repair note tells it to check what was already done. A crash does
  not consume a repair attempt.
- After the last interval the phase fails with the original cause, as before. Stop still interrupts at once, including
  during a wait. A process that dies between turns was already handled: the client reconnects on its next turn.

## Verification

- Build: zero warnings, zero errors.
- Full regression suite: **237 tests passed**: the 235 of 0.20.0 plus two new cases in `HardeningTests`: a provider
  that dies on its first two attempts is restarted twice with the configured backoff, the resumed turn carries the
  restart note, and the phase finishes; a provider that dies every time fails the phase after the last interval with
  the original cause.
- Package `artifacts\recovery-021-release` (file version 0.21.0.0) passed `tests\Local-Diagnostics-Smoke.ps1` and
  `tests\Conversation-Management-Smoke.ps1` (all four conversation-management cases).
- Live provider check: not run; the Codex account is over its limit until October 3, 2026.
- Clean checkout of commit `d5ce850` at a short temporary path built with zero warnings and passed all 237 tests
  against its own packaged bridge.
- Installed on September 28, 2026 after the idle 0.20.0 app was closed (no owned or running tasks, no child
  processes): `artifacts\recovery-021-install-result.json` (612 files verified, package hashes match, 10 profile files
  verified, production data unchanged, backups `before-collaboration-20260928-191652.zip` and
  `before-collaboration-data-20260928-191652.zip`). Reopened as 0.21.0.0 with 4 rooms and 1 task intact:
  `artifacts\recovery-021-reopen-result.json`.
