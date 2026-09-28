# Hardening — 0.16.0

Finishes the September 28 low-level design review: every finding that 0.15.0 left open. Issue numbers refer to
that review. The ledger, contract, ownership, evidence and audit rules are unchanged; the fixes sit behind them.

| Issue | Change | Where |
| --- | --- | --- |
| 2 Any incomplete snapshot makes review permanently impossible | Files over 8 MiB, and files beyond the 10,000-file / 128 MiB hashing budget, are fingerprinted by size and last write time instead of declaring a limitation. Change is still detected; a large asset no longer blocks `review_result` or `assignment_complete`. The hard entry cap is 50,000. | `ProjectSnapshot.cs` |
| 7 An abandoned run keeps mutating coordinator state after Stop gives up | The speaker and live dispatch are tagged with the run epoch; a stopped run's late cleanup clears them only if they are still its own, and Stop clears both immediately. | `HubCoordinator.cs` |
| 10 Unconditional saves and per-second ledger reads on the UI thread | The four-second save runs only when something changed (streamed text, drafts, routing, pauses, events). The Tasks window rebuilds its list and rereads the ledger only when a task's state, owner, update time or pending input changed. The progress answer reads the ledger off the UI thread. | `MainWindow.xaml.cs`, `MainWindow.Tasks.cs` |
| 12 Session re-keying kills the provider on the reader thread | `BindSession` returns the replaced id instead of throwing; the adapters report "started a new native session; … did not resume" as a status and the phase continues. | `CollaborationMcpHost.cs`, `ClaudeClient.cs`, `CodexClient.cs` |
| 13 Per-turn adapter state and cursors are unsynchronised | Both adapters reset and write per-turn state under one gate; the coordinator's context cursors are locked. | `ClaudeClient.cs`, `CodexClient.cs`, `HubCoordinator.cs` |
| 14 Startup loads and validates every ledger on the UI thread and caches all forever | Each ledger is recovered on its first load in the process; the desktop runs the startup pass in the background and shows any unreadable-ledger notice when it arrives. At most 16 ledgers stay cached (a task with an open dispatch is never evicted). | `CollaborationStore.cs`, `MainWindow.xaml.cs` |
| 15 Check reuse can never cross a process | The check environment hashes the OS and runtime versions, PATH, and the variables the operation names (`$env:NAME`, `%NAME%`, `${NAME}`, `$NAME`). | `SharedWork.cs` |
| 16 After a research split the second speaker is framed as a follow-up | Already resolved by the 0.14.0 opportunity queue: research completion no longer marks either participant as having contributed. Verified by inspection; no code change. | `HubCoordinator.cs` |
| 17 Room switches duplicate activity log lines | Reopening a room replays agent status into the cards only; the feed and log already hold those events. | `MainWindow.xaml.cs` (`ReflectStatus`) |
| 18 Double-escaped tool results can exceed the bridge frame | Relaxed JSON escaping on the wire (non-ASCII as UTF-8) and a 1 MiB frame, enough for a full 128,000-character record page quoted once. | `CollaborationMcpHost.cs` |
| 19 Preparation is discarded by benign tool events | Only a tool event with a native item id cancels a preparation; plan updates and approval notices do not. | `ConversationPreparation.cs` |
| 20 Small items | `InitialPauseReason` removed; one `ProjectStatusStore` per window; finished messages leave the streaming maps; a file already in a project subfolder is referenced in place; Codex stops asking after the first declined question, as Claude does. | `CollaborationGuard.cs`, `MainWindow.xaml.cs`, `WorkspaceImports.cs`, `CodexClient.cs` |
| Context-record cap (from issue 4) | At the 1,024-record cap the oldest conversation records are archived, agent replies before user messages; pins, notes, run prompts and anything a pin supersedes are never archived. A watermark keeps archived entries from being re-imported; the common context reports the archived count. | `TaskContext.cs` |

Not changed: per-room transcript files (issue 10's last item) remain a larger storage change; the eight-second
Stop wait and review scoping to named files are still as described in the review.

## Verification

- Build: zero warnings, zero errors.
- Full regression suite: **217 tests passed**: the 209 of 0.15.0, two updated to the new contracts (a re-keyed session
  is reported rather than fatal; a 9 MiB file keeps a snapshot complete, and a changed file is what sets a review
  aside), plus eight new cases in `HardeningTests`: oversized files fingerprinted by size and time with change still
  detected; a re-keyed session reported with tools still working; preparation surviving plan and approval notices but
  not real tool use; the curated check environment ignoring unrelated variables and tracking named ones; record
  archiving at the cap without re-import and with a persisted watermark; the bounded ledger cache reloading evicted
  tasks; a subfolder file referenced in place; Codex stopping after the first declined question.
- Package `artifacts\hardening-016-release` (file version 0.16.0.0) passed `tests\Local-Diagnostics-Smoke.ps1` and
  `tests\Conversation-Management-Smoke.ps1` (all four conversation-management cases).
- Live provider check: not run for this release; the Codex account's usage limit resets on October 3, 2026, and the
  0.14.0 live-stream check is still to be rerun then.
- Clean checkout of commit `f4fc861` at a short temporary path built with zero warnings and passed all 217 tests
  against its own packaged bridge.
- Installed on September 28, 2026 after the idle 0.15.0 app was closed (no owned or running tasks, no child
  processes): `artifacts\hardening-016-install-result.json` (611 files verified, package hashes match, 9 profile
  files verified, production data unchanged, backups `before-collaboration-20260928-163443.zip` and
  `before-collaboration-data-20260928-163443.zip`). Reopened as 0.16.0.0 with 3 rooms and 1 task intact:
  `artifacts\hardening-016-reopen-result.json`.
