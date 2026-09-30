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
