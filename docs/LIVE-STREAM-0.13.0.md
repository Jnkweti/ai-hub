# Resident sessions and the shared event stream — 0.13.0

This is migration step 1 of the target design recorded in the low-level design review: one shared
live stream that the user and both agents read, with the host serializing edits rather than speech.
Step 1 keeps the existing scheduler and changes what a turn is: the provider session is resident for
the whole phase, an ordered event stream is kept in the ledger and exposed to the agents, and later
turns in a phase receive only what happened since their last turn.

## Resident sessions

`CollaborationMcpHost` lives for a phase. The coordinator creates one host per agent per run and
attaches the current dispatch to it per turn (`Attach`), which also resets the per-dispatch call and
repair budget. Between turns the host is detached: the pipe stays open for the resident CLI, but tool
calls are refused with an explicit message and are not counted as repairs. The provider client is no
longer released after each dispatch, so Codex keeps its `app-server` thread and Claude keeps its
`--print` process across the phase. Clients and hosts are disposed together when the run ends, pauses,
fails or is stopped. A new user message is still a phase boundary and still starts fresh sessions.

## The event stream

`CollaborationDocument.Events` is an ordered, ledger-backed list of what every participant did:

| Kind | Appended by | Text |
| --- | --- | --- |
| `user_message`, `user_note` | `SynchronizeContext` when a new user record is imported | The message or note |
| `pinned_instruction` | `PinInstruction` | The pinned text and what it supersedes |
| `agent_message`, `agent_pass` | The turn loop after the terminal commit | The visible reply, or "Reviewed; nothing to add." |
| `research_request`, `research` | The turn loop; `publish_context` | The request summary; the published findings |
| `tool` | Evidence capture when a native command finishes | Tool, command (200 characters), exit code |
| `system` | Phase start and run end | Target, first speaker; the run's outcome |

Bounds: 2,048 events per task with the oldest evicted and counted, text clipped at 2,000 characters
with the referenced record kept, event kinds validated on load, additive defaults for 0.12 ledgers.
Agents read the stream with `get_events(after_sequence, limit ≤ 32)`; pages are bounded to 48,000
characters and report `evicted` and `has_more`. Peer and tool entries are attributed data; only user
entries carry user authority, exactly as in the common core.

## Delta prompts

A resident agent's later turns in a phase no longer receive the rebuilt common core. They receive the
events since its last turn, excluding its own, plus its assignment, the shared-conversation clause,
the incoming peer message, repair text and the preceding reply excerpt. The exact delta is saved as an
input manifest referencing the phase's common-core hash, so the inspector still shows precisely what
was sent. The first turn of each agent in a phase, and the synthesis turn after split research, still
receive the full core, because the research findings are supplied there.

## What did not change

Scheduling (first speaker, quiet passes, follow-ups, round cap), the contract, delivery states,
ownership, evidence, snapshots, work claims, preparation and delivery cursors are unchanged. Cursors
now matter only when a session resumes after a restart. Steps 2 and 3 of the target design, intent-based
turn-taking and the user as a peer, follow in later releases.

## Verification

- Build: zero warnings, zero errors.
- Full regression suite: **203 tests passed** (the 199 of 0.12.0, two of which were updated to assert
  the resident model, plus four new cases in `LiveStreamTests`): one provider per agent per phase with
  delta prompts after the first turn; event order, attribution, sequences and `get_events` paging;
  detached-host refusal and per-dispatch limit reset; event bounds, clipping, eviction and reload.
- Package: `Build.ps1` to `artifacts\live-stream-013-release`, 611 files, version 0.13.0, identical Core hashes in
  the app and the bridge (`artifacts\live-stream-013-build.txt`).
- Desktop smoke checks against the package: `tests\Local-Diagnostics-Smoke.ps1` and
  `tests\Conversation-Management-Smoke.ps1` passed (`artifacts\live-stream-013-local-diagnostics-ui.txt`,
  `artifacts\live-stream-013-conversation-ui.txt`).
- Live providers, first run (`artifacts\live-stream-013-native-routing`): with real Codex and Claude sessions
  and the packaged bridge, Codex opened a review request, Claude answered it, and Codex completed, all three
  ledger entries succeeding. Codex's two turns ran on one native thread and Claude's two turns on one
  `--print` process; each second turn was a delta prompt with no common core, routed through the resident
  bridge connection to a new dispatch. The stream recorded user, system, three agent contributions, a quiet
  pass and the run end. The check itself failed only because it predated 0.12.0's voluntary follow-ups and
  expected exactly three entries; the fourth was Claude's quiet pass after being invited to follow up.
- Live providers, final run (`artifacts\live-stream-013-native-routing-final`, log
  `live-stream-013-native-routing-final-log.txt`): with follow-ups disabled in the check, both directions
  passed. Codex → Claude → Codex and Claude → Codex → Claude completed their review round trips on resident
  sessions (one provider process per agent per phase, second turns as delta prompts on the same native
  session, the stream recording user, system, three contributions and the run end). The explicit
  single-agent continuation started a fresh phase with a new native session, retrieved history with
  `get_messages`, and appended a new generation and sequence. Completed records survived a restart with no
  automatic work, and the workspace stayed empty. Elapsed 118 seconds for both directions.
- Clean checkout of commit `5a7c4a7` cloned to a short temp path: Release build with zero warnings and zero
  errors; **203 tests passed** in 64 seconds (`artifacts\live-stream-013-clean-build.txt`,
  `artifacts\live-stream-013-clean-tests.txt`).
- Installed **0.13.0.0** on September 28, 2026 after the user closed the idle app (no running or owned
  tasks). All **611** package files matched the candidate and all **nine** production profile files were
  unchanged. Backups: `artifacts\before-collaboration-20260928-143822.zip` and
  `artifacts\before-collaboration-data-20260928-143822.zip`. Reopened the normal profile as 0.13.0.0 with
  zero running tasks and zero child processes. Evidence: `artifacts\live-stream-013-install-result.json`
  and `artifacts\live-stream-013-reopen-result.json`.
