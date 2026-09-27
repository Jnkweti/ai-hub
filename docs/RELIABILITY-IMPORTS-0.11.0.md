# Reliability, transcript sources and file imports — 0.11.0

Large pasted transcripts previously failed when the full room was imported into the bounded task ledger. A 282,824-character user message reproduced this in the installed app. Long user messages (over 24,000 UTF-8 bytes) now become task-scoped source references with their exact original saved separately. Agent replies over 128,000 characters use the same storage. Smaller records remain fully retrievable as before. No generated summary replaces the original.

Both providers and read-only researchers receive the source ID and hash. `read_context_source` reads exact chunks (up to 8,000 characters) or searches the full text from an offset with up to eight matches per call. Calls recheck task ownership after disk I/O. Previews are partial input, never proof of full coverage; whole-transcript analysis still requires reading the necessary ranges. Full originals also remain in the conversation and export. Missing or changed source files fail explicitly. Source files are included in profile backups and deleted with their owning conversation; deletion rollback restores them if removal fails.

**Import files** copies selected files into the current room's project folder and adds references to the saved draft. Nothing is sent automatically. Copies preserve bytes, use exclusive creation, keep existing names by adding numbered suffixes, and remove a newly created partial copy on failure. Files already directly inside the project folder are referenced without copying. Imported project files survive conversation deletion. Import does not convert file formats: provider-native reading tools determine which formats can be interpreted.

## Reliability corrections

- Nonempty ignored Git workspaces cannot certify an empty fingerprint.
- Freshness hashing and source reads run outside task/store state locks; ownership is checked again before committing or returning results. Main dispatch work starts off the desktop thread.
- Completed, unreferenced evidence is pruned at the 256-record limit. If every record is referenced or unfinished, the observation is explicitly omitted instead of terminating the provider. Counters expose pruning and omissions. Snapshot storage expires older entries at 1,024; a missing snapshot makes historical evidence unverifiable, never fresh.
- Invalid JSON ledgers are preserved and blocked. Orphaned ledgers remain saved with a recovery notice.
- Atomic replacement tolerates short Windows sharing conflicts with bounded retries (at most 350 ms of backoff); persistent failures preserve the original and retain rollback behavior.
- Codex retains multiple outward messages and treats provider interruption as a failure unless the user actually cancelled.
- Cleanup observes failed prior runs and disposes replaced dead connections. Tool failures return errors without killing the bridge listener.
- Structured delivery cursors reflect supplied complete records, not a discarded legacy prompt. Long preceding replies are bounded. Unicode context uses UTF-8 instead of unnecessary ASCII escapes; superseded objectives are not repeated.
- Claude Bash and PowerShell check evidence can complete matching claims. Missing exit status stays unknown and cannot establish a reusable successful check.
- Pin/replace provides a paused state and Continue action. The oversized-transcript failure can be retried in the existing task.

## Limits and intentional behavior

Both-selected messages still start concurrent preparation, including short messages, to honor the requested immediate participation policy. This can cost more than one provider call even when only one visible answer is useful. Single-agent selection skips peer preparation. Preparation has no source-reading tools; normal speaking/research assignments perform retrieval.

Limits are independent and the first one reached applies: 48,000-byte common context, 112,000-byte host input, 1,024 context records, 128 input manifests, 256 assignments, 512 structured messages, 128 shared-work records, 16 research sections and an 8 MiB ledger. Source originals are outside the ledger, up to four million characters per message. This release fixes a large message exhausting the ledger; it does not make task history unlimited. There is no fixed supported number of user turns. Environment-based check reuse remains deliberately conservative and does not cross task generations.

## Verification

- Clean checkout at implementation commit `a3aff53`: Release build with zero warnings/errors; **186 tests passed**, including immutable source retrieval, Unicode, restart, corruption, stale ownership, native adapters, import collisions and Windows sharing conflicts. Logs: `artifacts/reliability-011-clean-build.txt`, `reliability-011-clean-tests.txt`; checkout metadata: `reliability-011-clean-location.json`.
- Working checkout: 185 tests passed before the final sharing-conflict regression was added; all 26 focused reliability/source/import regressions passed afterward (`reliability-011-tests-final.txt`, `reliability-011-regressions.txt`). Fifteen isolated scheduling runs also passed.
- Original failed profile copied in isolation: its **282,824-character transcript** imported intact into the existing task; common context was **46,656 UTF-8 bytes**. Production data was untouched. Fixture: `artifacts/reliability-011-profile-copy`.
- Native Codex and Claude both retrieved different interior sections of one 280,614-character source through the packaged bridge. Host inputs were bounded; source previews were marked partial. Evidence: `reliability-011-native-source-final/results.json` and `events.json`.
- Native Claude Bash evidence completed a check claim with **unknown exit status and reuse disabled**, as intended (`reliability-011-native-claude/results.json`). Fake native-event regressions also cover explicit zero and nonzero exits for Bash and PowerShell.
- Native parallel researchers overlapped, saved findings, and resumed one synthesis without changing project files. This small fixture took 55.04 seconds / 5 calls / 39,020 host bytes versus 43.90 seconds / 1 call / 7,148 host bytes for one agent. No speed or cost advantage is claimed. Evidence: `reliability-011-native-research/comparison.json` and its results. Native runs preceded the final bounded-save retry; the final package's persistence was then verified by the full clean-checkout suite and desktop checks.
- Final package desktop checks passed for imports, filename collisions, saved drafts/restart, background tasks, progress polling, notes, explicit task separation, Stop all, shared-context pin/replace, and copied-profile upgrade without model work or transcript loss. Logs: `reliability-011-import-ui-final.txt`, `reliability-011-desktop.txt`, `reliability-011-context-ui.txt`, `reliability-011-upgrade.txt`.
- All 26 published schema fixtures, both plugin manifests, and all three bundled skill validators passed.

The desktop regression exposed an intermittent Windows destination-file sharing conflict; its activity stack identified `LocalStore.Save` during `ReleaseSpeaker`. Bounded replacement retries and a dedicated regression now cover that case. A separate bridge revocation timeout was fixed by isolating potentially blocking console input from the host EOF pump. Earlier failed attempts are not counted as passes.

Final installed package version: **0.11.0.0**. Core DLL SHA-256: `C573570F9327BD36C7C898F304F020A6B188BA41724031AEFA79984C74BD40B1`.

Installation: all **611 package files** matched the candidate; all **eight production profile files** were unchanged. Backups: `artifacts/before-collaboration-20260927-112314.zip` and `artifacts/before-collaboration-data-20260927-112314.zip`. Verification: `artifacts/reliability-011-install-result.json`.

Reopened the normal profile successfully with three rooms and message counts 55 / 7 / 16; no running tasks or provider children. Verification: `artifacts/reliability-011-reopen-result.json`. The pasted transcript is imported into source storage on the next explicit user phase; reopening does not dispatch or analyze it automatically.
