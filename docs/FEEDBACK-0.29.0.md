# Explicit feedback — 0.29.0

Phase 3 of [EMERGENCE-AND-LEARNING-PLAN.md](EMERGENCE-AND-LEARNING-PLAN.md): capture the developer's judgement of a
contribution or a task, keep it local and inspectable, link it to what it judges and to the outcome, and keep it
separate from anything inferred. This is the data the later preference and strategy stages will read; nothing in this
release changes agent behavior.

## What it does

| Surface | Behavior |
| --- | --- |
| Message feedback | A feedback button beside Copy on every completed Codex, Claude Code or AI Hub message opens a dialog: kind (useful / needs correction / preferred alternative), optional aspects (correctness, relevance, explanation, scope, initiative, collaboration, effort), an optional explanation, and where it applies (this task by default; this project; a named kind of task; in general). Saving shows a badge under the message ("You: needs correction · this project"). Opening the button again edits or deletes the record. |
| Task feedback | A Feedback button in the Tasks window records the same judgement on the task as a whole; the task's details show its feedback. |
| All feedback | An All feedback button in the Tasks window lists every record newest first, with its conversation; open to edit or delete; Export all writes Markdown. (A sidebar entry was tried first and dropped: at the compact window size it left room for too few conversations in the list.) |
| Exports | Conversation export gains a "Your feedback" section; a review packet gains "Your feedback (explicit, local)". |
| Deletion | Deleting a conversation removes its feedback with it. |

## What is stored

`%LOCALAPPDATA%\AIHub\feedback.json`, one `FeedbackRecord` per judgement (`Version` 1): the conversation, message,
dispatch, ledger-message and task IDs it links to; agent; kind; dimensions; explanation; scope and category; the
task's state and reason when recorded; the app version; a fingerprint of the coordination settings in force
(automatic collaboration, round cap, edits, models, mid-turn push, worktrees); and a SHA-256 of the judged text. The
text itself is not copied, so a later edit of the message is detectable without duplicating transcript content. To
make the message link reach the ledger, `SavedMessage` now records the host dispatch id for structured replies and
collaboration cards (`DispatchId`; older records load with it null).

The store validates every write (identifiers, lengths, known dimensions, a category when the scope needs one, an
explanation or a preferred message for "preferred alternative"), repairs a damaged file on load with the usual
`.unreadable-` backup, and caps at 4,096 records. No feedback is ever inferred from approvals, accepted edits or
silence, and the store is never read by any agent-facing path; the plan's "no feedback means unknown" is stated in
the export itself.

## Not in this release

Side-by-side comparison of real alternatives (a preferred alternative can name another message, nothing more);
feedback that reaches the agents (Phase 4 decides what, when and with what precedence); any learning.

## Verification

- Build: zero warnings, zero errors. Full regression suite: **264 tests passed** — five new cases: add / edit / delete
  with restart, validation rejections, repair of a damaged file with backup and the record cap, room deletion plus
  export content, and the saved-message dispatch id (repair drops an unsafe one; pre-0.29.0 records load).
- Desktop: a new native UI check, `tests\Feedback-Smoke.ps1`, seeds a conversation with one completed Codex reply from
  a structured dispatch, records "needs correction" through the dialog, finds the badge, checks `feedback.json` holds
  one record with the message, dispatch and room IDs, the explanation, 64-character hashes and none of the judged
  text, opens All feedback from the Tasks window and finds the entry, edits the same record in place to "useful", and
  confirms the file survives shutdown. It passed against the package, as did the two existing smoke checks. The first
  package had the list button in the sidebar; the conversation-management smoke caught that it left room for only two
  of three conversations at the compact 1080×700 size, so the button moved to the Tasks window.
- Package `artifacts\feedback-0290-release` (0.29.0.0) built with `Build.ps1 -Test`; installed on October 3, 2026
  after closing the idle 0.28.5 app (`artifacts\feedback-0290-install-result.json`: 612 files verified, hashes match,
  data unchanged, backups `before-collaboration-20261003-175731.zip` and `before-collaboration-data-20261003-175731.zip`);
  reopened as 0.29.0.0 with 5 rooms and 2 tasks.
