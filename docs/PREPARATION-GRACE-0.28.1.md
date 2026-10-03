# Preparation grace — 0.28.1

Two refinements from the [pilot baseline](PILOT-BASELINE-0.28.0.md), plus the diagnostic that justified the first.

## Finding

In the pilot's Both arm, Claude Code's tool-free preparation was recorded as `interrupted` and contributed nothing,
although Codex's first turn ran for 4 min 29 s, which was ample time for it to finish. The preparation client forwards
only usage events, so the activity log could not say why. A diagnostic runner (`AIHub.Tests.exe --preparation-live
<workspace> Claude <promptFile>`) ran one preparation alone on the same prompt: 56 s in total, 32 s of it thinking,
then ~4,600 characters of notes, with zero tool calls (preparation runs Claude Code with `--tools ""` and no MCP
servers, so a tool call could not have cancelled it). The fixed two-minute cap in `ConversationPreparation` was the
cause: under the load of two providers starting at once the preparation ran past it and was discarded while the first
speaker was still working. Evidence: `artifacts\pilot-028\preparation-claude.log`.

## Changes

| Change | Effect | Where |
| --- | --- | --- |
| Grace instead of a fixed cap | Preparation may run as long as the first speaker is still working, up to a ten-minute hard cap (`PreparationLimit`). When the peer's turn arrives and the notes are not ready, the turn waits `PreparationGrace` (45 s) for them, then continues without them; the unfinished preparation is cancelled and recorded as `interrupted`, with a status on the agent's card ("Preparation did not finish within 45 s of the turn; continuing with current context"). Notes that finish inside the grace are used as before. | `HubCoordinator.PreparationGrace`, `PreparationLimit`, the turn loop; `ConversationPreparation(…, limit)` |
| Restatement is a pass | The follow-up prompt now says that restating a peer's trace, numbers or conclusion in different words is a repeat, not an addition, and to pass instead. In the pilot, Codex's reaction turn restated Claude's hand trace; `CollaborationGuard.IsNearRepeat` is a near-duplicate detector (bigram overlap ≥ 0.85 with no new numbers) and cannot catch a paraphrase of a longer message, so this is handled by instruction and remains a known limitation. | `HubCoordinator.Speak` (follow-up text) |

## Verification

- Build: zero warnings, zero errors.
- Full regression suite: **257 tests passed** — the 255 of 0.28.0 plus two cases in `ConcurrentWorkTests`: notes that
  finish 200 ms after the lead's turn are used by the peer's turn and the preparation completes; notes that never
  finish are abandoned after a 300 ms grace, the peer's prompt carries no tentative notes, the preparation is
  recorded as interrupted and the status is reported. The existing preparation cases (frozen context, stop joins the
  preparation, failure fallback, no preparation for greetings, addressed messages or single-agent requests) pass
  unchanged.
- Live: the preparation diagnostic above (one Claude Code session, 56 s, 4,605 characters, no tool calls). The grace
  path itself was exercised by fixtures only; the next Both pilot run will show it live.
- Package `artifacts\prep-grace-0281-release` (file version 0.28.1.0) passed `tests\Local-Diagnostics-Smoke.ps1` and
  `tests\Conversation-Management-Smoke.ps1` (all four conversation-management cases).
- Installed on October 3, 2026 after closing the idle 0.28.0 app: `artifacts\prep-grace-0281-install-result.json`
  (612 files verified, package hashes match, 19 profile files verified, production data unchanged, backups
  `before-collaboration-20261003-160947.zip` and `before-collaboration-data-20261003-160947.zip`). Reopened as 0.28.1.0
  with 5 rooms and 2 tasks intact: `artifacts\prep-grace-0281-reopen-result.json`.
