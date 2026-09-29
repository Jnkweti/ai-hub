# Addressed messages and session carry — 0.26.0

From the activity log of a three-message "hello" conversation on September 29, 2026 (after 0.25.0 removed the cold
fingerprint): "what about you claude" took 21 s and three provider turns, because Codex was prepared for a message that
did not concern it and then spent 10 s deciding to pass; "what were your recs again?" took 70 s, because both agents
started fresh native sessions and re-read the same 22–25 k tokens of task context before answering. Two changes.

| Change | Effect | Where |
| --- | --- | --- |
| Addressed messages | With both agents selected, a message that names one agent ("Claude, …", "…, codex?", "@claude …") is that agent's to answer. No preparation session is started for the other, it gets no reaction turn, and its card shows "Not addressed; no reaction turn". The other agent is still dispatched when the reply asks it for something: an explicit `recipient` on the structured message, a handoff line, an opening or closing address, or an @mention (`ConversationTurns.AsksPeer`). A message with two different asks ("@claude do X, @codex do Y") still reaches both, and a message sent while the phase runs still offers a turn to both. | `HubCoordinator.RunAsync` (`addressed`, `addressedOnly`), `ConversationTurns.AsksPeer` |
| Session carry | A later phase of the same task resumes each agent's native session instead of starting a new one. The agent's first turn in the new phase is a delta prompt: the stream events since its last turn (the new user message, the peer's contributions, system notices), the assignment, and the user's current message repeated. The cursor saved after every turn now records the task, the last stream sequence shown, and the host input bytes fed to the session; a session is carried only when the cursor names the current task and the provider really resumed that session (otherwise the full core is sent as before). The phase-start system event lists the resumed sessions. An agent whose session is carried is not prepared either: preparation exists to parallelise a fresh read of the task context, which a carried session does not need. | `ConversationCursor.TaskId/StreamSequence/SessionInputBytes`, `HubCoordinator` (`carried`, `SessionCarryLimitBytes`), `SavedStateRepair.Cursor` |
| Plan-mode note for read-only Claude Code | Read-only rooms run Claude Code with `--permission-mode plan`. In two consecutive runs of the routing live check on this build, Claude Code decided that plan mode also forbade the host's `submit_message` tool, asked the user for permission (which a live check declines) and the phase failed after the repair budget; the same build's shared-conversation check had it submit normally. The appended system prompt for read-only collaboration sessions now states that plan mode means no file, shell or system changes, that the `ai_hub` tools are the pre-approved message channel, and that the terminal message must be submitted without asking. | `ClaudeClient.ReadOnlyNote` |
| Reset rule | A session is carried until the host has fed it `SessionCarryLimitBytes` (1,000,000 UTF-8 bytes of host prompts, roughly 250 k tokens); the next phase then starts it fresh with the full core and says so on the agent's card. The desktop's existing resets still apply: selecting another task, or changing the model, workspace or edit permission, clears the room's sessions and cursors. A synthesis turn after split research still receives the rebuilt core in the same session. | `HubCoordinator.SessionCarryLimitBytes`, `MainWindow.BuildHub` signature, Tasks window |

The delta prompt's wording changed with it: it now says the session continues from an earlier phase when it does, that
user entries in the stream (new messages, pins, notes) carry user authority, and the dispatch instructions tell agents
that a session is normally resumed by the next phase. Providers that fail to resume (Claude Code starting a new session
id, Codex rejecting the thread) already report it; the cursor then no longer matches and the following phase sends the
full core again.

Expected effect on the logged conversation: "what about you claude" becomes one Claude Code turn on a resumed session
(about 11 s instead of 21 s), and "what were your recs again?" becomes two short delta turns instead of two full
re-reads plus a preparation (well under half of 70 s). Real timings are in the verification section.

## Verification

- Build: zero warnings, zero errors.
- Full regression suite: **248 tests passed**: the 244 of 0.25.0 plus four cases: a message that names one agent
  dispatches only that agent, starts no preparation and reports why; an addressed agent whose reply hands off still
  reaches its teammate, with `AsksPeer` matching addresses and mentions but not a bare name; a cursor whose session the
  provider did not resume falls back to the full core, and a single-agent phase carries its session with the new
  message in the delta; saved-state repair keeps the carry fields and drops invalid values. The two-phase task-context
  case now expects the second phase to resume both sessions with delta prompts and a third phase past the carry limit
  to start fresh; the speaker-rotation case expects "Claude, discuss the tradeoff" to dispatch Claude Code alone; the
  large-message case accepts a delta prompt on the carried session. One run of the suite reported a timing failure in
  the pre-existing "Claude flood input requests" case (10-second fixture timeout under full load); it passed 12 of 12
  repeats in isolation and the whole suite passed on the second run.
- Live checks with the real CLIs (Codex CLI 0.159.1, Claude Code 2.1.284) on September 29, 2026:
  - `--shared-conversation-live` passed on the first run with the new expectations. The first phase (fresh sessions,
    lead plus prepared peer) took 28.9 s, three provider turns and 28,173 bytes of host input; the second phase, on
    carried sessions with no preparation, took 20.7 s, two turns and 8,532 bytes. The concise-answer phase and the
    unchanged-workspace check passed as before.
  - `--collaboration-routing-live` failed twice in its first phase before any new behaviour was exercised: Claude Code,
    reviewing in plan mode, reported plan mode as a blocker and then asked the user whether it was allowed to call
    `submit_message`. With the plan-mode note in place it passed all five legs, including both explicit continuations on
    carried sessions with delta prompts and the restart check.
- Package `artifacts\session-carry-026-release` (file version 0.26.0.0) passed `tests\Local-Diagnostics-Smoke.ps1` and
  `tests\Conversation-Management-Smoke.ps1` (all four conversation-management cases), rerun after the plan-mode note.
- Clean checkout, installation and reopen: recorded below once done.
