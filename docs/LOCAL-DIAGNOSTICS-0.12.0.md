# Voluntary follow-ups and local diagnostics — 0.12.0

Codex implemented the foundation of this release and handed it over in
[CLAUDE-HANDOFF-2026-09-27.md](CLAUDE-HANDOFF-2026-09-27.md); Claude Code completed the remaining
work listed there. The user's stated preference is local diagnostics plus on-demand review, with no
periodic model calls.

## Voluntary follow-up contributions

With **Both agents** and **Auto collaborate** on, the exchange no longer ends after the two initial
contributions. While the latest visible reply adds something, the other agent is invited once more
with an explicit optional framing: it receives the preceding reply as attributed peer data, is told it
has already contributed, and is asked to speak only for a specific addition or correction or to pass
quietly. A quiet pass, a repeated reply, a request for the user's input, a blocked status, the
configured round limit, single-agent targeting, Auto collaborate off, and the research synthesis turn
all end the implicit exchange. Explicit peer requests keep their existing routing and are not treated
as optional.

Repeat detection now compares a contribution against every visible reply of the phase (bounded to
102), using exact matching for any length and the existing near-repeat heuristic for replies up to
16,000 characters. New numerical evidence keeps its exemption. Heuristic similarity is not proof of
semantic duplication; a suppressed repeat is recorded as a diagnostic finding.

## Local diagnostics

`RuntimeAudit` keeps bounded, metadata-only diagnostics under the profile's `diagnostics\audit.json`:
at most 100 deduplicated findings and 200 recent event records, with older records evicted and counted.
A finding records category, count, first and last time, room and task references, the exception type
where one exists, and the application version that recorded it. Findings from different versions stay
separate so an upgrade can be compared with the release before it. Chat text, prompts, tool output,
file paths and exception messages are never stored.

Recorded categories: provider errors, conversation-save failures, saved-state recovery notices,
suppressed repeated contributions, round limits, suspected stalls (five minutes without progress in a
running room that is not waiting on an approval or question), and unhandled application errors.
Unhandled errors are recorded through the dispatcher, app-domain and unobserved-task hooks and flushed
immediately with a two-second bound; the failure itself is not suppressed, so crash behaviour is
unchanged. Ordinary persistence runs every 15 seconds and at orderly close.

Running rooms and pending approval or question waits are tracked separately. Turning collection off
and on while an approval card is open, or disposing, archiving or deleting a room, cannot produce a
spurious stall finding. Turning collection off stops new records and keeps existing findings. A
corrupt or oversized diagnostics file is ignored with a notice; conversation storage is unaffected,
and a later save may replace that file.

**Local diagnostics** in the navigation rail shows the report. **Export report** saves it as Markdown.
**Prepare agent review** creates a separate saved conversation addressed to both agents whose draft is
the review request plus the report; the user sends it when ready. That conversation is marked as an
audit review and its effective edit permission is forced off even when project edits are enabled
globally. No periodic or automatic model calls exist.

## Bridge shutdown correction

The MCP bridge crashed on orderly shutdown. After the host revoked the connection, the normal-close
path closed the pipe and then disposed the pipe's writer, whose flush threw `ObjectDisposedException`
outside the handled exception set. The unhandled exception handed the process to Windows Error
Reporting, which delayed exit by several seconds depending on machine state; the regression test
"revokes on stop" therefore passed on one machine and failed on another. The bridge no longer disposes
its reader and writer (AutoFlush leaves nothing buffered) and treats a closed pipe as a normal close.
The test now captures the bridge's stderr and reports how late an exit was, and the test runner accepts
`--only <text>` and `--repeat <n>` so timing-sensitive cases can be repeated in isolation.

## Limits and intentional behaviour

Findings document category, count, timing and references. They do not establish root cause, do not
create fixes, and do not include a UI-thread watchdog or semantic bug detection. A quiet interval may
be legitimate model or tool work. Startup failures before the collector exists are not recorded.
Persistence is best effort: an abrupt process termination can lose observations since the last flush.
Voluntary follow-ups add provider calls within the configured round limit; the limit is not a token or
spending cap.

## Compact layout correction

The handoff placed a full-width **Local diagnostics** button under **Tasks and notes**. In the
navigation rail the room list is the flexible area, so at the 1080×700 layout exercised by
`tests\Conversation-Management-Smoke.ps1` the extra row squeezed the list to a sliver and hid the third
conversation (see the failed run's `archived-compact.png`). The action is now an icon button beside
**Tasks and notes**, mirroring the status-options button, so the rail height matches 0.11.0.

## Verification

Every count below comes from a recorded run on September 27, 2026.

- Release solution build: zero warnings, zero errors (`dotnet build`), and the self-contained package
  built by `Build.ps1` to `artifacts\diagnostics-012-release` (611 files, version 0.12.0). Core DLL
  hashes match across the package, the packaged bridge and the test runner; log
  `artifacts\diagnostics-012-build.txt`.
- Bridge regression in isolation after the shutdown fix: `--only bridge --repeat 5` passed 10 of 10 in
  3.05 seconds. Before the fix the same test failed on this machine in three of three in-tree runs, the
  captured bridge stderr showing the `ObjectDisposedException` at `Program.cs:40`.
- Full regression suite from the test executable: **199 tests passed**, exit code 0, 67 seconds (the 195
  handed over plus four new cases).
- New regression cases in `FollowUpAuditTests`: lifecycle (toggle during an approval wait, forgotten
  rooms, clearing while disabled), per-version findings with an immediate unhandled-error flush and
  message exclusion, stop during a voluntary follow-up (assignment interrupted, ownership released, no
  visible message), and an explicit peer request followed by a voluntary follow-up (bounded to four
  dispatches, correct framing on each turn).
- Desktop smoke `tests\Local-Diagnostics-Smoke.ps1` against the package: report content, a finding
  seeded by an earlier version shown with its version, prepared read-only review room, restart
  persistence, and no conversation text in the diagnostics file. Log
  `artifacts\diagnostics-012-local-diagnostics-ui.txt`.
- Desktop smoke `tests\Conversation-Management-Smoke.ps1` against the package: four PASS lines after the
  layout correction; it had failed against the first 0.12.0 package and passed against installed 0.11.0.
  Log `artifacts\diagnostics-012-conversation-ui.txt`.
- Independent JSON Schema check: all 26 contract fixtures passed. `claude plugin validate` passed on the
  plugin manifest; the Codex CLI has no validate command, so its manifest was checked by parsing only.
- Clean checkout of commit `6979151` cloned to a short temp path: Release build with zero warnings and
  zero errors; **199 tests passed** from the test executable in 70 seconds. Logs:
  `artifacts\diagnostics-012-clean-build.txt` and `artifacts\diagnostics-012-clean-tests.txt`. The
  working-tree suite log is `artifacts\diagnostics-012-tests.txt`.
- Final package rebuilt from the committed tree: `artifacts\diagnostics-012-release`, product version
  `0.12.0+6979151…`, 611 files, Core DLL SHA-256
  `5AC059BF56D954AE6EB1FC6E144511433F59DE9F43AE55C7B3DC9BD1C2A4E87B` in both the app and the bridge.
  The clean-checkout Core DLL hashes differently because the build embeds its source path; its tests,
  not its bytes, are the evidence.
- No native model calls were made for this release.
- Installed **0.12.0.0** on September 28, 2026 with the user's approval after confirming the previous app
  was closed with no running or owned tasks. All **611** package files matched the candidate and all
  **nine** production profile files were unchanged. Backups:
  `artifacts\before-collaboration-20260928-135106.zip` and
  `artifacts\before-collaboration-data-20260928-135106.zip`. Reopened the normal profile as 0.12.0.0 with
  zero running tasks and zero child processes. Evidence: `artifacts\diagnostics-012-install-result.json`
  and `artifacts\diagnostics-012-reopen-result.json`.
