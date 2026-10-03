# Single-agent phase note — 0.28.2

From the [second pilot](PILOT-BASELINE-0.28.0.md): Claude Code, running alone, wrote "I'll … send Codex a focused
verification request", then discovered that Codex was not a selected participant and closed with a status instead.
The dispatch instructions and the packaged workflows describe a teammate, split research and peer review in every
phase, and nothing told a solo phase that none of that applies.

| Change | Effect | Where |
| --- | --- | --- |
| Single-agent phase note | When only one agent is selected, its host input ends with `SINGLE-AGENT PHASE: you are the only selected participant. There is no teammate to address, hand work to, or ask for a review; the packaged workflows' peer steps do not apply. Finish with a status message.` Two-agent phases are unchanged. | `HubCoordinator.Speak` |

## Verification

- Build: zero warnings, zero errors.
- Full regression suite: **257 tests passed**; the single-agent targeting case now asserts the note is present in a
  solo phase and absent in a two-agent phase.
- Live: the third pilot's solo arms ran on this build; Claude Code alone no longer attempted a peer request (see
  [the pilot document](PILOT-BASELINE-0.28.0.md)).
- Package `artifacts\solo-phase-0282-release` (file version 0.28.2.0) built with `Build.ps1 -Test` (257 tests) and
  passed both desktop smoke checks. Installed on October 3, 2026 after closing the idle 0.28.1 app (no provider child
  processes): `artifacts\solo-phase-0282-install-result.json` (612 files verified, hashes match, 19 profile files
  verified, production data unchanged, backups `before-collaboration-20261003-164217.zip` and
  `before-collaboration-data-20261003-164217.zip`). Reopened as 0.28.2.0 with 5 rooms and 2 tasks:
  `artifacts\solo-phase-0282-reopen-result.json`.
