# Verification — September 24, 2026

Current installed milestone: **0.6.0**, with durable task notes, ownership and workers that survive conversation switches. See [task-memory verification](TASK-MEMORY-VERIFICATION.md) for the build, 96-test core pass, native UI checks and package backups. The earlier checks below record their original versions and environments.

Verified on this Windows laptop with .NET SDK 10.0.103, Windows Desktop Runtime 10.0.3, Codex CLI 0.155.1, and Claude Code 2.1.282.

| Check | Result |
| --- | --- |
| Release build and publish | Passed |
| Offline regression runner | 13 checks passed |
| Actual Codex app-server initialization | Passed |
| Actual Claude Code control initialization | Passed |
| Real model replies from both providers | Passed |
| Second turn preserves conversation context | Passed for both providers |
| Terminate, reconnect, and resume native session | Passed for both providers; each recalled its test marker |
| Desktop automation sends through the actual composer and Send button | Passed |
| Automatic Claude-to-Codex and Codex-to-Claude handoffs | Observed through real provider replies and saved activity |
| Actual Stop button updates both agents to stopped | Passed |
| Closing the actual desktop window | Exit code 0 |
| Reopening saved conversation | Messages rendered successfully; no exchange restarted |
| Welcome and populated window screenshots | Inspected; dark theme and message layout rendered correctly |

Desktop smoke-test evidence is in `artifacts/desktop-smoke-14f8ae00f8ce4e4d9f816b791a2bc6f1/`. Screenshots are `docs/ai-hub.png` and `docs/ai-hub-conversation.png`. Test conversations were isolated from the normal app's data.

Approval allow/deny behavior, pending-approval cancellation, failure cleanup, and serialized editing were tested with fixture processes. Real-provider smoke tests used discussion mode and short messages that prohibited tools and file changes. They do not establish that every tool or approval variant works, and no long-duration resource/usage soak test was run. See the README for supported capabilities and limitations.

## v0.2 interface refresh

The refreshed release was built and published with zero compilation warnings or errors. All 13 collaboration regression checks passed again. The real desktop exchange test passed with both agents, handoffs in both directions, the Stop button, and clean window shutdown. New evidence: `artifacts/desktop-smoke-0f355d8a19f44fcc913ef66ea8234e58/`.

The offline appearance test verified activity-panel collapse/expand, custom maximize/restore/close, visible Send and Stop controls at 1080 x 700, editable composer text, and persistence of reduced motion. Evidence: `artifacts/appearance-smoke-aa884eec4f4d4bd4b79dd97a5728abc0/`.

The welcome screen and a populated conversation were visually inspected. Screenshots are `docs/ai-hub-v2.png` and `docs/ai-hub-v2-conversation.png`; the latter uses explicitly seeded sample content rather than actual agent findings. It verified headings, emphasis, bullets, block quotes, and tables. A separate code-block view was also captured during visual review. The installed Markdig package was checked against the configured NuGet vulnerability feeds; no reported vulnerabilities were returned at verification time.

## v0.2.1 keyboard, collaboration guard, and three usability passes

The release candidate in `artifacts/release-0.2.1/` was built successfully. All **28 automated regression checks passed**, covering provider lifecycle and approvals, greetings versus actual tasks, repeated replies, completion and requests for input, numerical evidence, tool activity, the default six-round limit, and resuming after a pause. The round counter is tested without a status subscriber, so its enforcement does not depend on the desktop UI.

| Desktop check | Result | Isolated evidence directory under `artifacts/` |
| --- | --- | --- |
| Actual Shift+Enter, Enter, and greeting with real providers | Newline inserted; one user message sent; both agents replied; no peer handoffs; visible pause reason | `keyboard-smoke-1e73f732dfe245d3a7b5d73f43eb2ed3` |
| Real provider collaboration | Both replies, both handoff directions, Stop, and clean shutdown passed | `desktop-smoke-f14362ee589f4a1aa1ed0da6fd69440d` |
| Drafts, recipients, pause recovery, and settings | Room switching and restart preserve drafts; Continue preserves unsent text; empty Send disabled; compact settings and round validation passed | `usability-smoke-6beb94d6730a437392399ac924b390c8` |
| Navigation | Search, empty results, clearing search, rename, keyboard shortcuts, copy feedback, and latest-message navigation passed | `navigation-smoke-3c1d0b1ca1f142f19a307c7667c36262` |
| Appearance | Activity panel, maximize/restore, controls at 1080 x 700, reduced-motion persistence, composer, and shutdown passed | `appearance-smoke-480987a4d2ba40cdae95d6ccc9db9898` |

`docs/ai-hub-guard.png` was visually inspected using the actual greeting conversation. It shows the saved guard notice and pause banner. Test conversations used isolated data directories and did not replace the user's conversation store.

The guard combines greeting recognition, explicit agent completion/input signals, repetition checks, and a deterministic round cap. It is not a general semantic proof that a task is complete. Real-provider checks used discussion mode; approval and editing behavior retain fixture coverage described above. Three review rounds and their root causes are recorded in `docs/USABILITY-REVIEW.md`.

## v0.3.0 mission-control redesign

The native WPF redesign was built and published successfully, with no compiler warnings or errors. The provider adapters, coordinator, and collaboration guard were unchanged; all 28 core regression tests passed. The release bundles verified static Sora and IBM Plex Sans fonts and their upstream OFL licenses, alongside the existing Lucide icons and Markdig renderer.

| Check | Result |
| --- | --- |
| Welcome and populated native windows | Visually reviewed; text-color inheritance corrected before final verification |
| Compact window at 1080 x 700 logical pixels | Passed at 144 DPI (150% Windows scaling); composer, recipient, automatic mode, Send, and Stop remain within the window |
| Automatic activity collapse and explicit visibility | Passed; compact windows reclaim reading space and an explicit choice survives resizing |
| Codex and Claude approval dialogs with fixtures | Passed; the waiting agent is identified, activity is revealed, and Decline works for both |
| Handoff instrument and guarded pause with fixtures | Passed; actual direction, paused agents, and round-limit reason agree |
| Drafts, selected conversation, recipient, continuation, settings | Passed through room switching and restart; compact settings and validation passed |
| Search, rename, shortcuts, copy feedback, latest messages | Passed |
| Window controls, activity transitions, reduced motion | Passed |
| Real Codex-Claude exchange | Both replies, both handoff directions, Stop, and clean shutdown passed |
| Actual Enter and Shift+Enter with real providers | Passed; hello receives both replies and no peer handoffs |
| Representative normal-text contrast pairs | Body 13.43:1, secondary text 5.99:1, primary button 10.25:1, Claude identity 7.81:1 |

Evidence under `artifacts/`:

- `mission-smoke-f214bc76955e43d2999b84a24a194b2b`: final fixture approvals, compact 150% scaling, handoff display, round-limit pause, and visibility checks.
- `desktop-smoke-6b7ad639c5ae4213bd3fa838f9f1f51d`: real-provider collaboration.
- `keyboard-smoke-63dd1f04b14f44259c0fb3eca72dee53`: final real-provider keyboard and hello check.
- `appearance-smoke-f4000030adc644d8b2476b03d7af538b`: window controls, activity transitions, reduced motion, and composer.
- `navigation-smoke-bec2a078d0404e2296dd6149993dd4d2`: search, rename, copy, shortcuts, and latest-message navigation.

Some chained automation runs could not resolve the newly opened window's controls. Individual fresh PowerShell processes and bounded control-readiness polling were used for verification. The documented test commands launch fresh processes. No model calls or project edits occur in the mission-control fixture test; real-provider checks use isolated discussion rooms. Contrast checks cover the listed pairs, not a complete accessibility audit or every Windows display configuration.

### Composer alignment and window icon follow-up

The shared TextBox template applied padding twice: WPF already forwards `TextBox.Padding` to `PART_ContentHost`, and the template repeated it as a margin. Removing that margin aligns the cursor with the placeholder and corrects the search and settings fields. The composer placeholder now binds to the same padding. Focused empty and populated composer captures at 150% scaling were visually checked in `artifacts/alignment-check-ae18b3c28a2045a4ae8a5460b03016df/`.

The shared Window style explicitly loads the packaged `aihub.ico`; the main and settings windows both use that style. The actual native window icon was retrieved with `WM_GETICON` and visually confirmed against the cyan app icon in the same evidence directory. The fixture keyboard check passed again (`keyboard-smoke-08c8bb6a5a3e4c72aeb42ad56163ae44`), as did the final appearance checks (`appearance-smoke-7aaf1e6d040940df939314e8a971dbe4`).

The taskbar retained the old purple icon even though icons extracted directly from the installed EXE and DLL were cyan. AI Hub now sets the stable Windows identity `AIHub.Desktop` before creating its UI. `Install-Shortcut.ps1` loads the same identity helper, applies it to the desktop shortcut and existing taskbar pins targeting this executable, sets their icon to `app/aihub.ico`, and notifies Windows of the changes. Shortcut property persistence and unchanged launch targets passed in an isolated check (`artifacts/identity-check/`). The final startup and keyboard check passed (`keyboard-smoke-fbf45edfb2504a93ae001635794e3695`).

The installed app was pinned through Windows' visible taskbar menu. Both shortcuts were verified to have the installed executable target, the cyan icon, and the matching AppUserModelID. `artifacts/taskbar-identity-installed.png` visually confirms the corrected cyan taskbar button. Windows reports one running pinned AI Hub window. The installed executable, assembly, and icon hashes match the tested release candidate, and the app was left open.

### Banner and header button placement

The top Claude Code banner now anchors its identifier, status, and detail to the right edge of its column. Its header previously stretched across that column and left the identifier toward the center of the instrument. Codex stays on the left, and handoff directions remain unchanged. Stop all now appears to the left of Activity, preserving the gap and both controls' handlers, accessibility names, and tooltips.

The combined release in `artifacts/ui-placement-20260924/` built without warnings or errors. The native mission-control fixture checks passed at 144 DPI, including compact controls, both approval dialogs, handoff direction, pause state, and activity visibility. Evidence: `artifacts/mission-smoke-eba5e9b6f2604784bdbcee8fe19dae1c/`. A separate minimum-width check with activity visible confirmed that both the Claude label and its longer pending-approval status ended at the same intended right edge (`mission-smoke-e18aea902de5417081667cb3d2fddca4/claude-needs-input.png`).

The installed build was backed up, both real agents were confirmed idle, and the update was installed with matching executable, assembly, and icon hashes. AI Hub was reopened from its existing desktop shortcut.

### Conversation archive and deletion

The isolated candidate is `artifacts/archive-delete-candidate/`. It builds without warnings or errors. All **32 offline core tests passed**: the existing 28 checks plus migration/default archive state, archive/restore context preservation, exact deletion scope and unsafe-ID rejection, and rollback when an activity log is locked. Test cleanup resolves and checks its directory before deleting fixture files.

The native fixture test `tests/Conversation-Management-Smoke.ps1` covers active/archived filtering and search, readable archives with disabled Send, explicit restore without launching providers, archived-room restart selection, retained drafts/recipients, cancel-delete preserving active work and an unsent draft, archive/delete while an approval is pending, owned-process and approval-window cleanup, deletion of exactly one activity log, unrelated-file preservation, and automatic replacement of the final active room. The compact archived view was checked at 1080 x 700 logical pixels and 144 DPI, including visible restore, archive, delete, and export controls.

Initial passing native evidence: `artifacts/conversation-smoke-92936d8cfd6744e9aeb50f531a54731d/`. `archived-compact.png` was visually reviewed. These checks used fixture providers and new `AIHUB_DATA_DIR` directories; no real conversations were archived or deleted. The existing Markdown export action remains enabled for archived rooms. The Windows Save As dialog did not expose working UI Automation patterns in the attempted export extension, so saving an exported file is not claimed as an automated check here.

Final candidate verification also passed immediate closure during an archive operation: the archive persists and all owned fixture providers exit. Final evidence under `artifacts/`:

- `conversation-smoke-8b752851a0af4d6a858d53e391cfa11f`: all conversation-management checks, including close during archive.
- `usability-smoke-74fa42854ff74115ae820e2a4706fb5b`: existing draft, recipient, restart, continuation, and settings checks.
- `navigation-smoke-8dea2bfb4e2640729748aca1493e84ea`: existing search, rename, copy, shortcuts, and latest-message checks.
- `keyboard-smoke-5717eadae38e4a54ba7c5ab8b8d27dd5`: actual Enter/Shift+Enter with fixture greeting guards.

The conversation and usability harnesses delay their initial `FromHandle` call until WPF has attached its automation provider. Attaching immediately when the HWND first appeared sometimes cached an early native stub with no child controls. No installed application, real data, or shortcut was changed by these feature tests; the candidate remains staged for integration.

## v0.4.0 shared project-status memory

The combined candidate is `artifacts/status-memory-candidate/`. It includes conversation archive/delete, the right-aligned Claude banner, Stop/Activity order, and isolated test-window identities. Builds pass without compiler warnings or errors.

The offline runner now has **47 passing tests** (32 existing plus 15 status checks): narrow status routing; content/add/delete fingerprints without Git; already-dirty file, index and branch changes with Git; a real second-process ownership lock and crash release; one sequential inspector/reviewer; warm reuse across conversations; simultaneous request reuse; explicit refresh and configuration changes; targeted recipients and preferred inspector; in-flight changes; malformed/unsupported evidence; scan-size limits; late canceled results; source-room deletion; cache expiry/corruption; and no status-session leakage or automatic exchange.

Native UI evidence under `artifacts/`:

- `status-smoke-afea681f4c934a2d9d476eafecf31a57`: final 0.4.0 status actions, cold/warm/stale/forced refresh, Forget, Continue returning to the status workflow, preferred inspector switch, restart reuse, ordinary-session/draft preservation, archive retention and source-room cache deletion. Both fixture adapters assert read-only status permissions even with project edits enabled in Settings.
- `conversation-smoke-ee42682143ea467f95c6d4802140950d`: combined archive/delete lifecycle checks, pending approval cancellation, exact deletion, final-room replacement and close during archive.
- `usability-smoke-7bd48f4f80f7463cbd35b5b9961652db`: combined settings, drafts, recipients, restart and pause recovery.
- `keyboard-smoke-d18e45e531f4485e97022e76eeb594db`: actual Enter/Shift+Enter and the greeting guard.

The new project controls initially left too little room for the conversation list at minimum height. Below 800 logical pixels, redundant Workspace/Conversations headings collapse and sidebar spacing tightens. The final compact layout at 1080 x 700 / 144 DPI was visually checked; archive restoration again exposes the expected conversation items. Menu-transition tests use bounded UI Automation readiness polling.

`status-live-20260924/` contains a real-provider check against a purpose-built two-file sample. Codex produced a valid inspection and Claude a valid focused review; both returned original usage events. An unchanged second request reused the complete report with **two provider turns for the cold run and zero additional turns for the warm run**. The sample files were not edited and no build/test execution was requested. `events.jsonl`, `assignments.jsonl`, `report.md` and the saved report preserve the evidence. This is a functional live check, not a general token-savings benchmark; cold CLI context overhead remains substantial. Provider usage fields are stored as emitted, without combining incompatible cache accounting or claiming unreported usage is zero.

Freshness certifies only the documented local source scope at check time. External/environment assumptions and model conclusions are not host-verified. Unsupported output fails visibly without a new complete cache. General task memory, automatic semantic deduplication and background tasks independent of the selected conversation are not part of this milestone.

### Broken pin repair

The taskbar failure was reproduced while the executable and shortcut targets were valid: actual pin activation failed, but launching the same `.lnk` directly succeeded. Explorer still associated the pin with an old path-based identity while the shortcut carried `AIHub.Desktop`. Ordinary native Unpin did not clear the stale registration; Windows' own broken-item removal did. Only the AI Hub entry was removed and recreated. Its orphan shortcut is backed up in `artifacts/taskbar-repair/AI Hub-stale-link-20260924-222342.lnk`.

After repinning with the current identity, closing the idle app and clicking the actual taskbar icon successfully launched the installed executable. No Explorer restart or global cache deletion was used. `WindowsAppIdentity.GetProcessAppId` now isolates `AIHUB_DATA_DIR` overrides into stable distinct identities; production and shortcut identity stay `AIHub.Desktop`. Fresh Windows PowerShell Add-Type compilation, identity normalization/separation and shortcut property checks passed.

### Installed release

Version 0.4.0 was installed after the final 47-test run and native checks. Both installed agents were confirmed idle before graceful shutdown. The previous app package is backed up at `artifacts/before-status-memory-20260924-224143.zip`. Every installed candidate file matches its staged SHA-256 hash. AI Hub reopened successfully with the new status/archive controls, and semantic comparison confirmed that conversation messages, drafts, recipients and ordinary native session IDs were preserved. The repaired taskbar shortcut's bytes and executable target were unchanged during installation.

Final closed-app taskbar verification at 22:42:59 EDT: the idle installed 0.4.0 window was closed gracefully, the actual taskbar pin was clicked, and Windows launched the responding installed `app/AI Hub.exe` version 0.4.0. Both taskbar and shortcut retained `AIHub.Desktop`; the shortcut bytes stayed unchanged. AI Hub was left open (PID 19304).

## v0.4.1 activity noise reduction

Root cause: `MainWindow.Handle` previously created one visible row for every status, token-usage and tool-output event. A latest-log sample of 1,500 events contained 1,429 tool-output fragments, including 726 whitespace fragments, plus repeated thinking/token updates. Tool lifecycle events and output were unrelated rows in the display.

`ActivityFeed` now groups a tool's start/output/completion into one updating action and provides readable titles from tool metadata. Routine status/usage and blank orphan output remain in diagnostics and the full log. Errors, warnings, approvals, plans, handoffs and result states remain visible. Separate limits keep raw-event bursts from evicting meaningful actions. Tool grouping is scoped by agent and dispatch, and malformed/out-of-order events have safe fallbacks. Action detail dialogs update in place; the agent banners no longer show output fragments.

The build passed without warnings or errors. **54 offline tests passed**, including seven new checks for a 1,500-chunk stream, noise filtering without hiding failures, cross-agent/turn ID reuse, failed tool results and exit codes, observable live output and important notices, bounded plan/output/history retention, and malformed/out-of-order metadata.

Native fixture evidence:

- `artifacts/activity-smoke-41a6a2ccd2d145c8a96d573875e15824/`: more than 500 raw events became six action rows; both agents' successful reads and failure results remained visible; grouped output was readable; diagnostics showed the bounded raw feed; the complete log preserved source events; the diagnostic preference survived restart. The populated panel was visually inspected.
- `artifacts/mission-smoke-9106e5ca293e4d36a8b68b72386cbef5/`: compact layout at 144 DPI, both providers' approval dialogs, handoffs, round-limit pause, accurate agent states and activity visibility passed.

No model calls were needed for this change. Original JSONL logging remains unfiltered; reducing log size or adding rotation is separate work. The native fixture checks inspect the full log file directly; launching the external Notepad window was not automated.

Version 0.4.1 was installed and reopened after verification. The previous package was backed up at `artifacts/before-activity-20260924-225818.zip`; staged/installed hashes matched, conversations were preserved, and the pinned shortcut's bytes and target were unchanged.

## v0.4.2 inline agent input

Root cause: both adapters appended option JSON to the question text, and `MainWindow.AskAsync` opened a separate free-text window for every question. The host had no structured choice UI or composer routing for a waiting request.

Questions and tool permissions now appear as transcript cards. Options preserve their labels/descriptions, Claude multi-select is supported, and Enter submits a selected choice or composer answer while Shift+Enter inserts a line. Replies complete the existing provider request instead of cancelling/restarting its turn. Separate transient question drafts preserve simultaneous questions and the ordinary room draft; a recipient label makes routing explicit. Questions survive as readable history with answered/declined/cancelled status, while callbacks and private answer text are not persisted. Codex secret inputs are masked and saved as a placeholder. Permission grants remain explicit Allow once/Decline actions.

The final build passed with zero warnings/errors, and **63 offline tests passed**. Nine added protocol checks cover both providers' choices and batched questions, exact answer-key/response formats, option descriptions and free text, Claude multi-select, decline, provider withdrawal, Stop cancellation, and Codex secret/closed-choice flags. Fixture messages now use unique item IDs and isolate the current question scenario from the shared transcript.

Native fixture evidence:

- `artifacts/input-smoke-8117fe8ea021414485dc6836f9028d45/`: final inline build; simultaneous agent cards without popups, no default/empty answer submission, Enter from a selected choice and from the composer, correct recipients, ordinary draft restoration, Claude multi-select, per-question drafts, Shift+Enter, multiline replies, masked private input without saved plaintext, compact layout, Stop cancellation, restart history and room-switch cleanup. Screenshots were visually inspected at 144 DPI.
- `artifacts/mission-smoke-d14b6c8b070a4dc791e6a65192a997b4/`: both providers' permission cards, pending agent state, explicit decline, handoffs, guarded pause, compact controls and activity visibility.
- `artifacts/conversation-smoke-5f5265caf2b841ac811633752655a190/`: archive/delete cancel pending cards and owned processes; cancelling deletion keeps work/drafts; exact deletion, archived read-only/restart/restore, final-room replacement and closing during archive passed.

These checks use real subprocess transports and native WPF controls with fixture providers; no live model calls were made for this feature. General MCP elicitation remains declined, as before; ordinary prose questions remain normal chat messages.

### Installed release

Version 0.4.2 was installed from `artifacts/input-candidate/` after final verification. The idle previous app was closed gracefully and backed up at `artifacts/before-input-20260924-231612.zip`. All candidate SHA-256 hashes matched the installed files. AI Hub reopened successfully (PID 9152 at verification). Semantic comparisons preserved conversation content, drafts, recipients, archive state and native session IDs; the newly optional Input field was normalized for comparison. The taskbar shortcut's bytes and target (`app/AI Hub.exe`) remained unchanged.

## v0.5.0 shared conversation and full host audit — September 25, 2026

The initial conversation fix was tested before the broader audit. Both discussion and editing now select one speaker, share its completed reply and inline human answers with the next speaker, and preserve per-agent delivery cursors across resumed native sessions. Explicit addressing, recent-speaker continuity, handoffs, passes, greetings, user-waiting pauses, provider failure and interruption are covered. The original defect and all audit findings/root causes are recorded in [AUDIT-2026-09-25.md](AUDIT-2026-09-25.md).

The final core/protocol runner passed **87 tests**, including real subprocess fixtures. New audit cases reproduce second-writer exclusion and crash release, invalid-state recovery with original backup preservation, atomic-save failure, relative-PATH executable rejection, bounded malformed/oversized stdout and stderr, repeated/failed-launch disposal, duplicate/flooded input requests for both providers, invalid question choices, deep project trees, and a delayed old cleanup that outlives Stop and must not affect its replacement. Output: `artifacts/audit-core-results.txt`.

All ten offline native WPF suites passed. The broad run is in `artifacts/audit-desktop-results.txt`. After the last lifecycle/input changes, these affected suites passed again on the final executable code:

- `audit-smoke-fa83a31c45db48c99b743167faac5fe8`: damaged records recover without losing valid messages, original backup bytes preserved, bounded long-message preview keeps the complete stored text, second launch reuses the original process, clean restart releases the lease, and a read-only activity log does not crash or get overwritten.
- `mission-smoke-ed0e32bc42b844d3ba91b140400c68f4`: native permission cards, current speaker, actual handoffs, automatic cap/pause, compact controls and activity visibility.
- `conversation-smoke-439da66560c2453d8c81f45c6d898016`: archive/delete, exact file scope, cancellation, owned provider cleanup, draft preservation and immediate close during a transition.
- `input-smoke-16fa86ae80384a26862a10f64602c0aa`: sequential questions, listening peer, Enter/Shift+Enter, choices/multi-select/free text, secret masking, Stop, room switching and restart.

These final results are in `artifacts/audit-final-desktop-results.txt`. The broad run also covers appearance, keyboard guard, usability/settings, navigation, project-status cold/warm/stale/refresh/forget behavior, and activity coalescing. Fixture providers were used throughout; no live provider calls were made for this audit. Compiler output contains no warnings/errors. The direct/transitive NuGet advisory scan reported no vulnerable packages (`artifacts/audit-dependency-scan.json`); this is not a blanket security guarantee.

### Installed release

Version **0.5.0.0** was installed from `artifacts/audit-candidate/` after the prior app's agent controls were confirmed idle and its window closed gracefully. Backups:

- Previous application: `artifacts/before-audit-20260925-002918.zip`.
- Local AI Hub data: `artifacts/before-audit-data-20260925-002917.zip`.

Every installed candidate file matches its staged SHA-256 hash. The app reopened successfully (PID 2968 at verification). Semantic comparison preserved conversation text, drafts, recipients, archive flags and native session IDs. The pinned taskbar shortcut's bytes and target stayed unchanged. The running app loaded the bundled `app/coreclr.dll`, whose Microsoft/.NET Authenticode signature verified as valid. The runtime configuration includes .NETCore.App and WindowsDesktop.App **10.0.12**. AI Hub's executable remains unsigned; global machine runtimes/SDKs were not changed. Upstream runtime license/third-party notices are included in the package. Full installation evidence: `artifacts/audit-install-result.json`.
