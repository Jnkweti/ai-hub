# Status report after a progress note — 0.27.0

A project status check on September 29, 2026 (fanfic workspace, 8:33 PM) ended with "The agent returned an
unsupported status report. No shared memory was updated" although both agents did their work. Codex's inspection
parsed. Claude Code's review returned a complete, well-formed report, but earlier in the same turn it had posted a
one-line progress note, as the review prompt asks ("Explain why in a visible tool/commentary event if additional
inspection beyond the cited files is needed"). `ClaudeClient` keeps every text message of a turn and
`CompletedReplyBuffer.Complete` joins them, so the reply that reached `ProjectStatusReport.Parse` was the note, a blank
line and the JSON object. The parser accepted only a bare object or a single code fence, so the whole review was
discarded.

| Change | Effect | Where |
| --- | --- | --- |
| Trailing report | `Parse` first tries the whole reply as before. If that is not a report, it tries each trailing part that starts a line with `{` or a code fence, from the earliest, and uses the first that deserializes. Notes before the report are skipped; text after the report is still rejected, so a reply that keeps talking after its JSON fails as before. Field rules (unknown members disallowed), size limits and evidence validation are unchanged, and a candidate that deserializes but fails validation is reported, not skipped. | `ProjectStatusReport.Parse`, `Trailing`, `Unfence` |

The review prompt keeps asking for visible commentary; the host now tolerates it instead of the model having to
choose between the two instructions.

## Verification

- Build: zero warnings, zero errors.
- Full regression suite: **249 tests passed**: the 248 of 0.26.0 plus one case: a report after a progress note, and a
  fenced report after a note, are accepted and saved; a report followed by more text, and a note followed by broken
  JSON, are rejected without reaching the reviewer or the cache.
- Clean checkout of commit `7e131c7` (a fresh clone at a short temporary path, since a Codex session had uncommitted
  edits in the workspace) built with zero warnings and passed all 249 tests again via `Build.ps1 -Test`.
- Package `artifacts\status-parse-027-release` (file version 0.27.0.0) passed `tests\Local-Diagnostics-Smoke.ps1` and
  `tests\Conversation-Management-Smoke.ps1` (all four conversation-management cases).
- Installed on September 29, 2026 after the idle 0.26.0 app was closed (no owned tasks, no provider processes; a
  Notepad window it had opened on an activity log was left open): `artifacts\status-parse-027-install-result.json`
  (612 files verified, package hashes match, 19 profile files verified, production data unchanged, backups
  `before-collaboration-20260929-205648.zip` and `before-collaboration-data-20260929-205648.zip`). Reopened as
  0.27.0.0 with 5 rooms and 2 tasks intact: `artifacts\status-parse-027-reopen-result.json`.
- README is not updated in this release: it had uncommitted edits from a concurrent Codex session, and the version
  line is added once that work is committed.