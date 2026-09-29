# Default Codex model — 0.24.0

OpenAI released GPT-6.1 Sol for Codex on September 29, 2026 (near-Astra agentic performance at about a fifth of the
cost). AI Hub now uses it by default.

## How it works

- When the Codex model in Settings is blank, AI Hub starts and resumes Codex threads with `gpt-6.1-sol`
  (`CodexClient.DefaultModel`) instead of leaving the choice to the CLI's own configuration. A model typed in Settings
  still wins.
- If this Codex install or account rejects the model, the client falls back to the CLI's own default: a rejection when
  the thread starts is retried without a model; a rejection from the service once the turn runs (the error names the
  model as unknown, unsupported, not supported, invalid or unavailable) makes the client reconnect the same thread
  without a model and ask once more. Either way a status notice says so, and the rejection is remembered for the rest of
  the session so later phases skip straight to the CLI default. An explicit model in Settings is never substituted; its
  error is reported as before.
- Found live on September 29, 2026: with a ChatGPT-account login the service answers "The 'gpt-6.1-sol' model is not
  supported when using Codex with a ChatGPT account" at turn time, so on this account the default falls back to the CLI
  default (`gpt-6-astra` from `~/.codex/config.toml`) until the account gains access. The installed CLI 0.157.1 also
  lacks the model in its registry; it appears from CLI 0.159.0.
- Also: sending a message to a single agent that the app had marked "unavailable until …" now clears that mark and
  sends, since the user's send is their word that the provider is back. A fresh limit reply re-marks it.

## Verification

- Build: zero warnings, zero errors.
- Full regression suite: **242 tests passed**: the 241 of 0.23.0 plus one case: a blank model becomes `gpt-6.1-sol`,
  an explicit model is used as given, and in both rejection modes (at thread start, and from the service at turn time
  with the real ChatGPT-account error text) the client falls back to the CLI default with the notice, a later client
  skips the failed attempt, and an explicitly chosen model is never substituted.
- Package `artifacts\default-model-024-release` (file version 0.24.0.0) passed `tests\Local-Diagnostics-Smoke.ps1` and
  `tests\Conversation-Management-Smoke.ps1` (all four conversation-management cases).
- Live, September 29, 2026, real Codex and Claude Code CLIs with the packaged bridge, Codex signed in with a ChatGPT
  account: `AIHub.Tests.exe --collaboration-routing-live` first failed with "The 'gpt-6.1-sol' model is not supported
  when using Codex with a ChatGPT account" (the finding that shaped the turn-time fallback), then passed all five
  steps after the fallback was added: durable review round trips in both orderings on resident sessions, explicit
  continuations with fresh phases, and records surviving a restart. `AIHub.Tests.exe --live-stream-live` passed:
  3 contributions, 1 pass, 2 provider starts, a mid-phase user message received by each agent's next turn, outcome
  "Every participant passed". Evidence under `%TEMP%\ah-live-024-routing` and `%TEMP%\ah-live-024-stream`.
- The live-stream check's assertion was corrected for the resident-session model: only each agent's first turn after
  the interjection must carry it, since later deltas contain only newer events.
- PENDING: clean checkout verification, installation.
