# AI Hub shared context implementation checklist

Status: completed, verified, installed, and reopened as 0.9.0 on September 27, 2026. Supersedes the uninstalled 0.8.0 candidate. The original 0.7.0 package and production data are backed up; installation preserved all production profile files.

## Intended behavior

AI Hub owns the durable conversation and task state. Each provider receives a versioned common briefing plus its assignment. Provider reasoning and native working histories remain separate. Important shared information is supplied automatically; tools retrieve additional detail. Both agents can contribute directly to the user without a permanent model leader or a mandatory duplicate answer.

## Design review and corrections

- [x] Reject a literal merged model context/KV cache: provider interfaces do not expose that capability. Share application state and record exactly what the host supplied.
- [x] Replace identical exhaustive prompts with a common task core and separate assignment detail. Preserve independent investigation and disagreement.
- [x] Identify cursor defect: post-turn transcript IDs can include omitted text and messages that arrived during execution. Track actual included fragments; only fully supplied unchanged messages count as delivered.
- [x] Separate context delivery from model comprehension. UI and reports say supplied, never understood or permanently remembered.
- [x] Separate authoritative user instructions from agent claims, tool observations, generated summaries, and host scheduling metadata. Models cannot create user decisions or edit host provenance.
- [x] Preserve original records. Use bounded extractive history with source IDs instead of repeatedly rewriting a lossy authoritative summary. Essential instructions cannot silently fall out of context.
- [x] Version shared state independently from file freshness. Scoped source hashes detect changes; matching context versions do not certify files or conclusions.
- [x] Use stable record IDs and atomic replacement for retries. A failed/uncertain model or tool run must not be replayed automatically after restart.
- [x] Preserve conflicting claims. No last-writer-wins truth or automatic promotion of agreement to verification.
- [x] Keep editing serialized. Parallel read-only research remains available; dependent work waits for the required results, unrelated work can continue in other tasks.
- [x] Treat current two-area research as a bounded join: its synthesis explicitly depends on both areas. Partial results remain available and are labeled incomplete if a provider fails.
- [x] Treat a user message as a new work phase. Start fresh native sessions from the canonical briefing at phase boundaries, retaining native continuity within the phase. This provides a testable reconstruction boundary without restarting during tool work.
- [x] Distinguish progress questions from steering. Conservative exact progress requests get a host answer without stopping or starting models. Other messages retain explicit stop/cleanup before replacement work; no guessing that a correction is harmless.
- [x] Do not promise semantic duplicate detection. Simple conversational requests can avoid a second dispatch; substantive optional checks can publish a quiet pass. Required review remains explicit.
- [x] Bound context by UTF-8 bytes and expose actual sizes. These are host input bounds, not fabricated provider token counts; native instructions/tools and provider context limits remain separate.
- [x] Avoid an extra coordinator model, embedding service, or database migration in this release. Use existing provider adapters, task claims, and atomic task ledger.
- [x] Preserve scope: task state is isolated by room/task/workspace. No global memory promotion, inferred user authorization, or cross-task replay.

## 1. Canonical state and lifecycle

- [x] Add additive versioned context records and input manifests to the task ledger; old records remain loadable.
- [x] Capture complete task conversation entries with stable IDs, author/type, timestamps, and source references. Reject ambiguous ID reuse.
- [x] Persist user instructions and user-pinned notes without silent truncation; allow explicit user supersession while retaining history.
- [x] Keep agent findings and disputed claims attributed. Include existing review/evidence records and research provenance by reference.
- [x] Record per-dispatch assignments, dependencies, expected outcome, run generation, and lifecycle. Failed/interrupted work stays distinguishable from completed contributions.
- [x] Validate loaded state and enforce bounds before writes. Preserve malformed/unsupported files for recovery.
- [x] Preserve restart/no-autoreplay, archive, task isolation, atomic writes, deletion, and rollback semantics.

## 2. Context construction and exact delivery

- [x] Implement a context builder with deterministic common content, byte budget, source manifest, omissions, and common-content hash.
- [x] Automatically include active user instructions, pinned decisions, task identity, relevant recent conversation, assignments, unresolved findings, and bounded shared research with freshness labels.
- [x] Keep large/older detail retrievable using bounded task-scoped record lookup; preserve full originals and explicit omission references.
- [x] Stop with an actionable message if essential instructions cannot fit; never silently drop them to fit optional history.
- [x] Persist the exact host prompt before dispatch. Record prepared/responded/failed/interrupted outcomes without claiming native retention or comprehension; assignment completion requires a terminal commit.
- [x] Fix legacy cursors to record only fully delivered unchanged message content; keep omitted/partial/new arrivals eligible for delivery.
- [x] Give parallel researchers the same frozen common snapshot and different assignments; include both findings automatically at the dependent continuation.
- [x] Reconstruct fresh provider sessions at phase boundaries. Preserve ongoing native tool/approval continuity within the phase.

## 3. Scheduling and user participation

- [x] Extract deterministic participation policy: explicit recipient, rotating first speaker, focused peer requests, optional checks, simple-answer fast path.
- [x] Preserve quiet passes and prevent duplicate final events from creating repeated chat messages. Do not hide actual blockers or required review.
- [x] Answer narrow progress questions from persisted/live host state without canceling workers, changing generations, or calling either provider.
- [x] Ensure corrections stop affected execution before replacement work. Reject late context publication and maintain workspace ownership through cleanup.
- [x] Share inline user answers with the next dispatch. User-pinned corrections stop the affected task before changing authoritative state.
- [x] Retain configured turn limits, one split-research request per user message, bounded repairs, and no automatic restart on failure.

## 4. Inspectable desktop and packaged workflows

- [x] Extend Shared context with active instructions, superseded history, attributed findings, assignments, omissions, and exact recent input manifests.
- [x] Add user controls to pin and supersede instructions. Explain that changing pinned instructions stops affected work before updating it.
- [x] Keep routine research detail in shared context and useful conversation in chat; expose quiet participation through activity.
- [x] Export shared state and input provenance with the conversation/task report.
- [x] Update bundled provider workflows and release documentation; validate plugin/skills and keep provider-neutral data formats.

## 5. Acceptance and regression verification

- [x] Context selection: omitted and partial messages are never marked fully delivered; late arrivals and edited content remain eligible.
- [x] Persistence: snapshots/manifests survive restart; invalid versions/data are preserved; task deletion removes associated context; uncertain work never replays.
- [x] Reconstruction: both fresh providers receive the same active user constraints and current shared facts with separate assignments.
- [x] Corrections: stop both affected researchers, revoke stale publications, and supply the correction before replacement work.
- [x] Authority: agents cannot forge user instructions; supersession preserves history; conflicting claims remain visible.
- [x] Research: real concurrency, isolated permissions, automatic sharing, freshness changes, partial failure, and cancellation.
- [x] Conversation: meaningful distinct contributions, silent passes, one visible simple answer, no duplicate message-event rendering.
- [x] Human interaction: a progress poll leaves workers and generation unchanged; inline answers reach the next worker; pinned edits take effect safely.
- [x] Bounds: oversized essential input fails explicitly; optional omissions and byte counts are inspectable; retrieval and retries remain bounded.
- [x] Run existing regression suite and meaningful new tests. Run independent JSON Schema parity and workflow validation.
- [x] Run real Codex/Claude scenarios in disposable profiles using the packaged bridge; inspect results, not just exit codes.
- [x] Run desktop UI checks for context inspection, pin/supersede, conversation continuity, and existing task behavior.
- [x] Record elapsed time, provider calls, and host input sizes for representative single-agent and collaborative fixtures. Do not generalize one fixture into a performance guarantee.

## 6. Release

- [x] Build a clean self-contained 0.9.0 candidate and verify package/version/bridge/workflows.
- [x] Recheck the production app is idle. Close normally, preserving drafts; do not terminate active user work for installation.
- [x] Back up installed app and production data, install the verified candidate, and verify package hashes and unchanged profile files.
- [x] Reopen the normal app/profile and confirm version/launch without starting agents.
- [x] Record evidence, remaining limitations, and completed checklist items. No claim of completion for unchecked required work.

## Explicit limits

Models retain separate inference state and may interpret the same supplied facts differently. Delivery records do not prove understanding. Source freshness does not prove conclusions. Provider internal compaction is opaque to this host. Semantic novelty and natural-language conflict resolution remain model judgments. Arbitrary parallel edits, third-party deployments, hidden-reasoning transfer, and a general autonomous task DAG planner are outside this release; current explicit assignments and bounded research dependencies are supported.

## Evidence log

Design reviewed against ConversationTurns, HubCoordinator, TaskMemory, CollaborationStore, SharedContext, ContextResearchWorkflow, native clients, desktop send/input handling, and the previous release tests.

Additional flaws found and fixed during execution: room repair discarded delivery hashes; failed work was labeled interrupted; task notes silently dropped after 20 entries; Claude's last-result-only handling discarded useful earlier text; freshness hashing held state locks; and imported chat timestamps could misorder notes and user corrections. Each correction has regression or desktop evidence.

- Final regression suite: **151 passed**, `artifacts/context-redesign-tests-release.txt`; release build: zero warnings/errors, `artifacts/context-redesign-build-release.txt`.
- Independent schema validation: 26 fixtures passed. Both plugin manifests are 0.9.0; plugin and all three workflow skill validators passed.
- Real Codex/Claude discussion, fresh session reconstruction, rotating lead, quiet participation, and one visible simple answer: `artifacts/context-redesign-native-discussion-final/results.json`.
- Real overlapping researchers, identical common snapshot hashes, automatic synthesis context, unchanged files, and measured single-agent comparison: `artifacts/context-redesign-native-research-final/results.json` and `comparison.json`.
- Final desktop task/progress/notes/restart checks: `artifacts/context-redesign-desktop-release-log.txt`. Final shared-context/pin/supersede/restart checks: `artifacts/context-redesign-context-ui-release-log.txt`.
- Copied production profile upgrade: `artifacts/context-redesign-upgrade-log.txt`.
- Final installation: `artifacts/context-redesign-release-install-result.json` — 611 package hashes matched; all eight production profile files unchanged during installation.
- Reopened normal profile: `artifacts/context-redesign-reopen-result.json` — version 0.9.0.0, zero running tasks, zero child processes.

See [release details and measured limitations](SHARED-CONTEXT-0.9.0.md). Native scenarios preceded the final timestamp-only adjustment; the final package passed all 151 regression cases and the desktop checks afterward. No implementation work remains unchecked within this release scope.
