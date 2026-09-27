# Durable task milestone verification — 0.6.0

The implemented milestone saves task objectives, notes, ownership and recent agent replies, and retains room workers when the user changes conversations. It adds a workspace task inspector and explicit new-task/selection controls. Automatic decomposition, dependency scheduling, SQLite storage and reusable general project facts remain future work.

## Core checks

The complete offline suite passed **96 tests** (87 previous checks plus nine task-memory checks). Evidence: [tasks-final-core-results.txt](../artifacts/tasks-final-core-results.txt).

The new checks cover durable notes/replies and interrupted-run recovery, duplicate task and editing-workspace claims, independent reads, stale generations and wrong-owner replies, bounded isolated briefings, rollback after failed atomic writes, conversation-scoped deletion and recovery, coordinator failure reporting, supplied task context, and a stopped worker that deliberately remains alive beyond Stop's five-second wait. The delayed worker retains its editing claim until it actually exits and cannot publish a late result.

The existing checks retain coverage for provider protocol pipes, inline questions, shared turn ordering, cancellation races, malformed output, single-instance ownership, project-status freshness and retention, activity projection, and saved-state recovery.

Tests used fixture providers and isolated directories. No paid model call or user project edit was performed. Fixture Git commits used a process-only `commit.gpgsign=false` override to avoid the laptop's unrelated signing setup; global Git configuration was unchanged.

## Build

The Windows x64 self-contained candidate is `artifacts/tasks-candidate/`, version 0.6.0.0, retaining the pinned .NET 10.0.12 runtime. Compilation and publishing completed without compiler warnings or errors. Package dependencies were unchanged. The initial online NuGet advisory lookup could not reach nuget.org; the offline build disabled that lookup. This milestone does not claim a fresh dependency-advisory scan.

## Desktop checks and installation

The task desktop suite passed preservation of legacy native sessions when opening an older room, independent concurrent reads, background-result routing, note editing and briefing reuse, explicit new-task isolation, persistence after restart without automatic dispatch, and Stop all cancellation of an offscreen worker. Evidence: [tasks-desktop-pass.txt](../artifacts/tasks-desktop-pass.txt).

The inline-input suite passed sequential choices and listening-peer routing, multiple selections, multiline answers and Enter/Shift+Enter behavior, private-answer masking, compact-window controls, cancellation and restart, and switching away from a pending question and returning to answer its original provider. Evidence: [tasks-input-results.txt](../artifacts/tasks-input-results.txt). The keyboard checks ran outside the execution sandbox because Windows blocked synthetic key delivery inside it; the test verifies that its fixture window is foreground before sending keys.

The previous app package and local data were backed up to `artifacts/before-task-memory-20260926-235346.zip` and `artifacts/before-task-memory-data-20260926-235346.zip`. Version 0.6.0.0 was copied into the existing `app/` directory with **411 package files verified by SHA-256**. The production conversation file's hash was unchanged, and shortcut targets were not edited. The installed app was not launched and no production history migration or model turn was triggered during installation. Exact evidence: [tasks-install-result.json](../artifacts/tasks-install-result.json).

## Limits

Task completion text remains an agent claim; a finished exchange is not verification of the objective. Saved replies may be stale and are labeled accordingly. Notes remain local plaintext. Editing exclusion uses normalized workspace paths within one application profile; different filesystem aliases or separate data profiles are not a cross-machine or OS-level lock. Workers do not continue after the app closes, and interrupted work never resumes automatically.

The registry retains atomic JSON under the existing exclusive application owner. Task and conversation files are individually atomic, rather than one crash-atomic transaction. Ordinary deletion errors restore notes; a process interruption between deletion writes can leave orphan records. The inspector hides records whose source conversation is absent.
