# Shared context redesign — 0.9.0

The host owns durable task state; providers retain separate inference contexts. New user phases start fresh native sessions reconstructed from active original user instructions, attributed conversation, assignments, review findings, and shared research. Within-phase continuity remains native. Context tools retrieve omitted originals. Both read-only researchers receive the same frozen common core with different assignments; synthesis receives their findings automatically.

Original instructions are never silently dropped. User pin/replacement controls preserve superseded history and stop affected work before changing authoritative state. Agents have no pin/replacement tool. Conflicting agent claims remain attributed. Existing task notes are retained rather than silently removing older notes. A context version or current-file hash is not a correctness certificate.

Desktop transcript imports preserve original message timestamps, and active instructions interleave chat, notes, and pins chronologically. Untimestamped external/legacy entries use first-observed time. Explicit supersession retains history regardless of timestamps, and the current user message is supplied separately. This avoids treating import time as the time an older user instruction was issued.

Each dispatch saves its exact host prompt before calling a provider, with SHA-256 hashes, revision, included/partial/omitted record IDs, UTF-8 byte count, task generation, assignment, provider, and outcome. A returned reply means responded; successful assignment completion additionally requires the structured terminal commit. Restart interrupts uncertain records and never replays commands. Older ledgers receive additive defaults; unsupported or malformed state is preserved and blocked.

Participation remains host-coordinated: explicit recipients, rotating initial speaker, bounded handoffs, optional quiet peer contributions, and single-contribution paths for greetings/exact short answers. Structured prose is withheld until terminal commit and emitted once, avoiding duplicate native final events. Live tools/status remain visible. Progress questions read host state without canceling a worker. Other messages stop affected execution before replacement work.

Live verification found that Claude can place useful text before a tool call and end with only an acknowledgement. The adapter now assembles completed outward messages across the turn, coalesces native IDs, and removes exact duplicate finals under the existing reply bound. This preserves the answer when chat is withheld until commit. Source freshness checks run outside shared-state locks and off the desktop thread so inspection and cancellation remain responsive.

Bounds: common core 48,000 UTF-8 bytes; full host prompt 112,000 bytes; 1,024 original context records; 128 input manifests; 256 assignments; 8 MiB ledger. Research retains its existing 16-section limit. Full storage stops explicitly and keeps saved records. These are host limits, not provider token counts. Provider system prompts, tools, private history, and internal compaction are outside the manifest. The task inspector shows six recent exact inputs; originals and manifests remain in the ledger.

Each canonical record accepts up to 128,000 characters; pinned instructions accept 4,000. Essential-size checks include the task objective and serialized instructions, so a very large task may need to be separated even before the total storage limit is reached. Bounds produce explicit errors and retain the saved transcript.

The reviewed checklist is [SHARED-CONTEXT-IMPLEMENTATION-CHECKLIST.md](SHARED-CONTEXT-IMPLEMENTATION-CHECKLIST.md). Provider-neutral message schema remains 1.0 with the existing context_request addition. The bundled Codex/Claude workflow manifests are 0.9.0; no global plugin installation is required. Rollback must restore the matching pre-update app and data backups because older binaries cannot interpret every newer record.

## Verification

Observed checks:

- Release build: zero warnings/errors; self-contained Windows x64 package with the MCP bridge and three validated 0.9.0 workflows.
- Independent JSON Schema validation: all 26 contract fixtures passed. Plugin and all three skill validators passed.
- Native conversation: two unaddressed discussion turns reached both providers, the lead rotated, actual session IDs changed at the phase boundary, original constraints remained supplied, and the simple-answer phase produced one visible `4` with one provider call. Completed Claude content before a tool call survived reply assembly. Workspace stayed unchanged. Evidence: `artifacts/context-redesign-native-discussion-final/results.json` and its log.
- Native research: both workers overlapped, their input manifests shared one common hash, both findings were persisted and automatically supplied to synthesis, and files stayed unchanged. Evidence: `artifacts/context-redesign-native-research-final/results.json` and `comparison.json`.
- Desktop: shared context showed sources, freshness, exact inputs, pin/replacement history, and persisted replacements after restart. Task checks covered noninterrupting progress polls, independent background rooms, notes on follow-up, task isolation, one greeting answer, restart without replay, and Stop all. Logs: `artifacts/context-redesign-context-ui-final-log.txt` and `context-redesign-desktop-final-log.txt`.
- Upgrade: a copy of the existing production profile opened and closed with three rooms, one task, transcripts, and drafts preserved; no model work started and the original profile hashes stayed unchanged. Evidence: `artifacts/context-redesign-upgrade-log.txt`.

The final timestamp adjustment is covered by a regression that interleaves an old chat message, a later note, and the newest correction. The final full suite passed **151 tests** (`artifacts/context-redesign-tests-release.txt`). The timestamp-corrected desktop passed task/progress/notes/restart and shared-context pin/replacement checks (`context-redesign-desktop-release-log.txt`, `context-redesign-context-ui-release-log.txt`). The tested Core assembly, packaged app, and packaged bridge had identical Core hashes.

Final package: `artifacts/context-redesign-release`. Installation verified all **611** package file hashes and left all **eight** production profile files unchanged. The app reopened as **0.9.0.0** in the normal `%LOCALAPPDATA%\AIHub` profile with zero running tasks and zero child processes. Evidence: `artifacts/context-redesign-release-install-result.json` and `context-redesign-reopen-result.json`.

Original pre-update 0.7.0 backups: `artifacts/before-collaboration-20260927-021115.zip` and `artifacts/before-collaboration-data-20260927-021115.zip`. A second package/data backup before the timestamp-corrected installation is recorded in the final installation result. Restore matching app and data backups together if rolling back.

One two-file research fixture produced these observations:

| Mode | Elapsed seconds | Provider calls | Host input bytes |
| --- | ---: | ---: | ---: |
| Collaborative investigation | 77.03 | 5 | 45,615 |
| Single Codex investigation | 32.36 | 1 | 5,700 |

Parallel workers overlapped, but orchestration and synthesis made collaboration slower and larger on this tiny fixture. These are single-run observations with provider/network variability, not a general performance estimate. Host bytes exclude provider instructions, tools, and native history. Split research remains intended for substantial independent discovery.
