# Claude handoff: voluntary contributions and local auditing

The user asked Codex to finish its current work so Claude can take over. The user's explicit audit preference is **local diagnostics plus on-demand review**, with no periodic model calls. These changes are in source only; the installed app and production profile have not been replaced or modified by this work. The desktop version remains 0.11.0 until release verification.

## Implemented in this handoff

- `HubCoordinator`: after both initial contributions, Auto collaborate can invite the peer again when the latest reply adds content. Follow-ups receive the latest peer output, an explicit optional-follow-up instruction, and peer attribution rather than another user dispatch. A quiet pass or suppressed repeat ends the implicit exchange. Single-agent targeting and Auto collaborate off do not create extra voluntary turns. Input waits and the existing round cap stop the exchange. Split context research still ends with one synthesis unless an explicit peer request continues the work.
- Repeat detection checks prior visible contributions in the phase (bounded to 102 replies). Near-repeat detection uses the existing heuristic only for replies up to 16,000 characters; exact duplicate checking also handles longer replies. New-number evidence retains the existing exemption. Heuristic similarity is not proof of semantic duplication.
- `RuntimeAudit`: bounded local metadata (100 deduplicated findings, 200 recent events), background persistence every 15 seconds and on normal shutdown, exportable report, provider errors, conversation-save errors, recovery notices, repeat suppression, round limits and suspected five-minute inactivity. Approval/question waits suppress inactivity findings. Provider/user text and exception messages are excluded; only exception type is recorded where available. Disabled collection retains previous findings. Storage failure stays isolated from conversation state.
- Desktop: **Local diagnostics** button opens a report, with export and **Prepare agent review** actions. Preparing creates a separate saved draft addressed to both agents. The user sends the draft to trigger the review. `Room.IsAuditReview` persists and forces provider/coordinator edit permissions off even if global edits are enabled. Existing background room workers are retained.
- Settings: local collection toggle and updated collaboration description.
- Added 9 regression cases in `FollowUpAuditTests`; existing routing fixtures explicitly disable voluntary follow-ups so their original routing scenarios remain isolated. Dedicated new cases enable it.

## Verification

- Release solution build completed with zero warnings and zero errors.
- Full regression suite: **195 tests passed**, exit code 0. Output: `artifacts/follow-up-audit-tests.log`.
- `git diff --check` passed (Git emitted only its existing line-ending normalization notice).
- No native model calls, desktop UI automation, packaging, installation, or production restart were performed for this handoff.

## Remaining work before releasing

1. Review the new scheduler semantics, especially explicit handoffs mixed with optional turns, user steering/cancellation during follow-ups, quiet passes, and fresh/resumed native sessions. Add direct follow-up cancellation and live native-provider coverage. Existing broad cancellation tests do not prove every new scheduling branch.
2. Exercise the diagnostics UI using an isolated profile: export, prepare draft, send to both agents, settings changes, restart, active background tasks and window close. Verify the provider command lines actually remain read-only in audit rooms. The current automated permission test covers the effective-permission policy, not a native command-line/UI round trip.
3. Harden audit lifecycle: active-room observations are capped but a removed/disposed worker should explicitly clear its audit running state, otherwise an old room can eventually receive a spurious suspected-stall finding. Turning collection off/on while an approval is already pending also needs a focused lifecycle test.
4. Review metadata retention across upgrades: the report currently prints the running application version, not a separate version for each historical finding. Add per-record version/session/run identifiers if required for reliable multi-version diagnosis.
5. Unhandled application crash hooks are **not wired** (`UnhandledError` is reserved). Persistence flushes normally every 15 seconds and at orderly close; a crash can lose the newest observations. Startup failures before the collector is created and all possible storage/logging errors are not covered. There is no UI-thread watchdog or automatic semantic flaw detector. Corrupt diagnostic JSON falls back to an in-memory empty snapshot and shows a notice; later saving may replace that diagnostic file. Task/conversation files are unaffected.
6. Decide whether richer stack fingerprints and resolution/status tracking are needed. Current findings document category, count, first/last timestamps and room/task references; they do not establish root cause or automatically create fixes. Reviews are deliberately read-only and on demand.
7. Validate the final package with existing smoke scripts, update the version and release docs, then install only once the production app has no active work. Preserve production data and installation backups. Do not overwrite an active app or claim this source change is installed.

## Outcome

Claude Code completed the remaining work on September 27, 2026. Items 1 to 5 above were addressed, item 6
was decided against for this release, and item 7 is complete up to installation, which waits for the
user. Two further problems were found and fixed on the way: the MCP bridge crashed on orderly shutdown,
which made the "revokes on stop" test timing-dependent, and the full-width diagnostics button collapsed
the room list at compact window sizes. Evidence and details are in
[LOCAL-DIAGNOSTICS-0.12.0.md](LOCAL-DIAGNOSTICS-0.12.0.md) and its
[checklist](LOCAL-DIAGNOSTICS-0.12.0-CHECKLIST.md).

## Useful commands and existing evidence

Build: `dotnet build 'AI Hub.slnx' -c Release --no-restore --nologo -v minimal -m:1 -p:UseSharedCompilation=false`

Tests: run `tests/AIHub.Tests/bin/Release/net10.0/AIHub.Tests.exe` directly (fixtures use `Environment.ProcessPath`; do not substitute `dotnet AIHub.Tests.dll`). Native subprocess and named-pipe fixtures require normal Windows access beyond the restricted sandbox.

Wait for test/UI/native processes to exit before rebuilding the same output paths; simultaneous rebuilds can lock fixture binaries.

Previous installed-release evidence and preservation procedure: `docs/RELIABILITY-IMPORTS-0.11.0.md`. Latest pre-handoff commit was `40adb08`. Production data is under `%LOCALAPPDATA%/AIHub`; installed executable is `app/AI Hub.exe`. Recheck actual current state before installation.
