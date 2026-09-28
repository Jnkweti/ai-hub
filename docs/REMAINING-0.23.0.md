# Remaining review items — 0.23.0

The last open items in `docs/TODO.md` that do not depend on a provider account: two carried over from the September 28
design review, and the test runner's crash-on-failure.

| Item | Change | Where |
| --- | --- | --- |
| Per-room transcript files (review issue 10) | `rooms.json` is now a small index of room headers; each room's messages live in `room-<id>.json`. A legacy single-file `rooms.json` is split on first load. The periodic save writes the index and only the transcripts that changed: rooms the window marked dirty (streamed text, "[Stopped]" suffixes, input status changes) or whose message count differs from the last write. Deleting a room removes its transcript. | `LocalStore.LoadRooms`, `SaveRooms`, `DeleteRoom`; `Room.Header`; `SavedStateRepair.Messages`; `MainWindow` dirty tracking |
| Review freshness scoped to named files (review issue 2) | A review's snapshot now records the hashes of the files the review names (files, or everything under named directories, up to 512). A review, or a finding, is fresh when the whole tree is unchanged or when exactly those files are unchanged, even if unrelated files moved on. Evidence records still use the whole tree. | `CollaborationSnapshot.ScopedFiles`, `CollaborationStore.Fresh`, `ScopedHashes`, `WithScope` |
| Test runner reports failures | A failing test prints `FAIL <name>: <exception>` and the run continues; the summary lists failures and the exit code is 1 when any failed, instead of the process crashing with an unhandled exception. | `tests/AIHub.Tests/Program.cs` |

Still deferred: the live provider checks (the Codex account is over its limit until October 3, 2026), including mid-turn
push and worktrees with the real CLIs.

## Verification

- Build: zero warnings, zero errors.
- Full regression suite: **241 tests passed**: the 239 of 0.22.0 plus a rooms-storage case (a legacy two-room
  `rooms.json` with 300 messages is split into an index without message text and per-room transcripts; an unchanged
  transcript keeps its write time across a save while another room grows; a transcript marked dirty is rewritten; the
  round trip preserves data; deleting a room removes its transcript and index entry) and a scoped-freshness case (a
  review of `src/main.cs` is accepted and reported fresh after an unrelated file changed, and rejected after the named
  file changed). The runner itself now continues past a failure.
- Package `artifacts\remaining-023-release` (file version 0.23.0.0) passed `tests\Local-Diagnostics-Smoke.ps1` and
  `tests\Conversation-Management-Smoke.ps1`, whose restart, archive, delete and scoped-deletion cases exercise the new
  storage.
- Live provider check: not run; the Codex account is over its limit until October 3, 2026.
- PENDING: clean checkout verification, installation (the installed profile's 396 KB `rooms.json` migrates on first launch).
