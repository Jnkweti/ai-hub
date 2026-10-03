# Execution routing for read-only Claude Code — 0.28.3

From the [pilot baseline](PILOT-BASELINE-0.28.0.md): across nine runs the only factual error was a hand trace by
Claude Code running in plan mode, which cannot execute anything; Codex ran the reproduction whenever it was in a
position to. The harness already has everything needed to route that step — a `question` to the teammate, host-captured
command evidence, and the 0.28.0 resolution turn that brings the answer back — but nothing told Claude Code to use it.

| Change | Effect | Where |
| --- | --- | --- |
| Routing note | The read-only note appended to Claude Code's system prompt now says: when a conclusion depends on running a command and a teammate is selected, do not hand-trace the result; submit a `question` to the teammate naming the exact command and what its output would settle. The teammate can run read-only commands, the host captures the output as evidence, and the answer returns for the asker's decision. Solo phases are unaffected (the advice is conditioned on a teammate, and 0.28.2's single-agent note applies). | `ClaudeClient.ReadOnlyNote` |

## Verification

- Build: zero warnings, zero errors. Full regression suite: **258 tests passed** (one new case pins the note and its
  teammate condition).
- Live (`artifacts\pilot-028\routed5`, the first pilot task addressed to Claude Code): Claude found the cause from
  source and, unprompted, sent Codex a `question` asking it to run the reproduction and the tests — the first
  unprescribed question in eleven runs. Codex ran the CLI (captured as evidence, matching Claude's prediction) but
  pytest cannot create its capture file in the read-only sandbox and the runner denied escalation, so Codex answered
  `blocked` and the run paused for the user before a resolution turn. Details and the two frictions this exposed
  (claim matching against the shell wrapper; a blocked answer not reaching the asker) are in the
  [pilot document](PILOT-BASELINE-0.28.0.md).
- Package `artifacts\exec-routing-0283-release` (0.28.3.0) passed both desktop smoke checks; installed on October 3,
  2026 after closing the idle 0.28.2 app (`artifacts\exec-routing-0283-install-result.json`: 612 files verified,
  hashes match, data unchanged, backups `before-collaboration-20261003-170254.zip` and
  `before-collaboration-data-20261003-170254.zip`); reopened as 0.28.3.0 with 5 rooms and 2 tasks.
