# AI Hub 0.7.0 collaboration release

Prepared September 27, 2026. The collaboration release is implemented and verified; installation evidence is recorded below.

## Delivered implementation

- The desktop uses durable structured dispatches for ordinary tasks, with the same task, ownership, generation, identity, and idempotency checks for Codex and Claude.
- Five MCP tools: `get_task_context`, `get_messages`, `get_evidence`, `submit_message`, and `mark_addressed`.
- Captured native command/read evidence retains provider, source event, command, bounded output, timestamps, nullable exit code, before/after snapshots, and provenance. Unknown values remain unknown. Results are captured from provider events, not independently rerun by the host.
- Review scope and snapshot must match the request. Changes and incomplete coverage reject review reuse. Finding IDs persist through open, addressed, and checked states. An author claim cannot check a finding; fresh successful execution or read evidence from a different agent is required. Unresolved/stale findings prevent assignment-complete acceptance.
- Expandable collaboration cards, task history, captured-output inspection, current review freshness, and Markdown export. Archives retain history; deletion removes only the room's task ledgers. Invalid histories remain preserved and unavailable for dispatch while other tasks can open.
- The app bundles a self-contained .NET 10.0.12 MCP bridge and one plugin with Codex/Claude manifests and three validated workflow skills. AI Hub loads those skill files into each provider's instructions. Global plugin configuration and marketplace installation are unnecessary.

## Verification

- Full solution build: zero warnings/errors, `artifacts/collaboration-final-build-results.txt`. Final desktop publish includes the evidence-window accessibility correction: `artifacts/collaboration-final-desktop-publish.txt`.
- **124 regression tests passed**, including stale evidence, scope changes, unknown exit codes, output bounds, finding transitions, incomplete fingerprints, and files changed during command execution: `artifacts/collaboration-final-core-results.txt`.
- Independent Python JSON Schema validation passed all **23 shared fixtures**. The candidate plugin and all three packaged skills passed the supplied validators.
- Inline input checks passed choices, free text, multi-select, masked private answers, correct recipients, cancellation, compact layout, restart, and pending questions across room switches: `artifacts/collaboration-final-input-results.txt`.
- Final task UI checks passed durable structured cards in background rooms, evidence inspection, notes, task isolation, restart without auto-run, and Stop all: `artifacts/collaboration-final-task-ui-results.txt`.
- Archive/delete checks passed filtering, read-only archive views, restoration, scoped deletion, cancellation of owned providers and approvals, replacement rooms, restart, and close during archive: `artifacts/collaboration-final-conversation-results.txt`.
- The actual packaged desktop passed a read-only native test with Codex and Claude replies, structured handoffs in both directions, Stop and clean shutdown: `artifacts/collaboration-final-desktop-live-results.txt`. Reopening its isolated profile for a visual check started no work. The screenshot is `artifacts/collaboration-release.png`.
- NuGet advisory query completed successfully with no vulnerable direct/transitive packages in the configured sources: `artifacts/collaboration-vulnerability-check.txt`.
- Native versions: Codex CLI **0.157.1**, Claude Code **2.1.283**. Final editing workflow evidence is under `artifacts/collaboration-workflow-live-20260927-final/`; each successful role order requires five committed messages, a stable finding ID, author fix, peer check, captured evidence, and final assignment status.
- Both editing role orders passed: Codex → Claude → Codex → Claude → Codex (11 evidence records), and Claude → Codex → Claude → Codex → Claude (9 evidence records). Each completed five committed messages and checked finding `add-negative`. An independent host rerun of each resulting `check.py` printed `ALL CHECKS PASSED` with exit code 0. The test runner, desktop candidate, and packaged bridge use identical Core assembly hashes.

Earlier exploratory runs are retained separately. The first workflow fixture failed its exact-sequence assertion because its Claude start instruction allowed an initial Codex handoff; the final fixture explicitly addresses the starting agent. Desktop fixtures were updated to send structured requests and to recognize Standby after a completed dispatch's native process is released. A new evidence-window accessibility label fixes its automation/screen-reader identity; the UI fixture also waits for WPF's automation provider before caching its window. The final reruns listed above passed.

## Practical boundaries

Source snapshots cover the documented Git/folder scope, not every dependency, external service, or machine setting. Before/after fingerprints cannot detect a change that is made and fully reverted between captures. Output is bounded and remains untrusted data. A successful command or read establishes its observed result, not the truth of every interpretation; the host does not certify arbitrary claims.

Claude's native shell records may omit exit codes. Such codes stay unknown; its successful captured file reads can support a source-review finding. A command-only checked result needs an explicit successful exit status. No arbitrary command runner is exposed through MCP.

History is bounded to 512 messages, 256 evidence records, 512 current findings, 1,024 snapshots and 8 MiB per task. Limits stop further work visibly and retain prior records; there is no silent pruning. Multiple persistence files are not one cross-file transaction. Startup marks unfinished work interrupted and never automatically replays native execution. Recovery does not promise exactly-once model execution.

No claim of reduced token use or improved model quality is made from these integration checks. Existing activity logs retain supplied provider usage, message timestamps, tool failures and handoff events for later measurement.

## Installation

The tested package was installed to `app/` as **0.7.0.0**. All **611 files** matched the staged package; all **6 production profile files** remained unchanged. The production profile was not launched. `artifacts/collaboration-install-result.json` records the final installation and backup paths; `artifacts/collaboration-candidate-manifest.json` records package hashes.

The original pre-release app/data backups are `artifacts/before-collaboration-20260927-010225.zip` and `artifacts/before-collaboration-data-20260927-010225.zip`. A final visual check found a pre-existing hardcoded 0.5.0 footer; it now reads the assembly version automatically. That presentation-only correction was republished, reopened in the isolated profile, and included in final installation verification.
