# Hardening — 0.15.0

Fixes for the highest-impact findings of the September 28 low-level design review, chosen for blast
radius and for being provable by the regression suite. Issue numbers refer to that review.

| Issue | Change | Where |
| --- | --- | --- |
| 1 Full workspace fingerprint on every tool event on the reader thread | Evidence capture runs on a sequential background chain per dispatch; the coordinator drains it before validating the terminal message and before aborting. Capture frequency is unchanged. | `CollaborationStore.cs` (`Enqueue`, `DrainAsync`), `HubCoordinator.cs` |
| 3 Stale review snapshot fails the run | The stale review is set aside as Interrupted and a validation error asks the same dispatch to re-check and resubmit; a set-aside terminal no longer blocks a replacement. | `CollaborationStore.cs` (`Complete`, `LiveTerminal`) |
| 6 Claim leak when the final task write fails | The claim is released before persisting; on failure the live task entry still ends in memory. | `TaskMemory.cs` (`End`) |
| 8 Transport hiccups and read-tool argument errors count as repairs | Only an invalid `submit_message` spends the three-repair budget. | `CollaborationMcpHost.cs` |
| 9 Pipe listener dies on a non-cancellation error | The serve loop retries after a short delay instead of faulting for the phase. | `CollaborationMcpHost.cs` |
| 4 Hard cap of 128 manifests kills long tasks | Prompt text is kept for the newest 24 manifests (hash, size and ids remain for all); at the cap, manifests of earlier phases are evicted oldest-first. Only a single phase producing more than 128 inputs still fails, explicitly. | `TaskContext.cs` (`PruneInputs`, `PromptRetained`) |
| 5 Whole transcript re-imported every turn | Entries whose record already exists with identical content are skipped before any hashing or file I/O; large entries are compared by length and hash; an inline original that has become large still migrates to source storage. | `TaskContext.cs` (`SynchronizeContext`), `ContextSources.cs` |
| 11 Window close can hang forever | Worker disposal is bounded to 20 seconds and diagnostics disposal to 5; failures are recorded and the close always completes. | `MainWindow.xaml.cs` |

Not changed in this release: issue 2 (any file over 8 MiB makes reviews impossible) is a policy choice about what
a fingerprint certifies and is left for a decision; issues 7, 10, 12 to 20 remain as documented in the review.
The 1,024 context-record cap remains; manifest pruning removes the more frequent failure.

## Verification

- Build: zero warnings, zero errors.
- Full regression suite: **209 tests passed**: the 205 of 0.14.0, one updated to expect the set-aside review and a
  successful replacement terminal, plus four new cases in `HardeningTests`: a failed final task write releases the
  claim; only invalid submissions spend the repair budget; manifests are pruned oldest-first across three phases
  with prompt text retained for the newest 24 and the pruned ledger reloading; evidence emitted during a turn is
  recorded on the background chain and drained before the commit.
- Package `artifacts\hardening-015-release` (file version 0.15.0.0) passed `tests\Local-Diagnostics-Smoke.ps1` and
  `tests\Conversation-Management-Smoke.ps1` (all four conversation-management cases).
- Live provider check: not run for this release. The Codex account's usage limit resets on October 3, 2026, and the
  changes are host-side; the 0.14.0 live-stream check is still to be rerun then.
- Clean checkout of commit `2b96e68` at a short temporary path built with zero warnings and passed all 209 tests
  against its own packaged bridge.
- Installed on September 28, 2026 after the idle 0.14.0 app was closed (one stopped task, no owner, no child
  processes): `artifacts\hardening-015-install-result.json` (611 files verified, package hashes match, 9 profile
  files verified, production data unchanged, backups `before-collaboration-20260928-155040.zip` and
  `before-collaboration-data-20260928-155040.zip`). Reopened as 0.15.0.0 with 3 rooms and 1 task intact:
  `artifacts\hardening-015-reopen-result.json`.
