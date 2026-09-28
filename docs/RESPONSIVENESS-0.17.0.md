# Responsiveness — 0.17.0

Why a one-word message took a minute, measured from the activity log of a real conversation on September 28, 2026
(a personal writing project: 3,277 git-listed files, 134 MB):

| Seconds after send | What happened |
| --- | --- |
| 0 to 1 | Codex preparation session started (failed on the usage limit); Claude Code started and connected. |
| 4 to 11 | Claude read the structured prompt and thought. |
| 11 to 45 | Claude called `submit_message`; the host spent 34 seconds fingerprinting the whole workspace inside that call. |
| 45 to 55 | Claude wrote its prose; the CLI reported the result. |
| 55 to 58 | Codex was dispatched for its opportunity and failed on the usage limit; the whole run paused. |

Three fixes, one per cause:

| Cause | Change | Where |
| --- | --- | --- |
| Every snapshot rehashed the whole tree | A content hash is reused for any file whose length and last write time are unchanged since an earlier capture of the same workspace, so a capture costs one listing and a stat per file. The fingerprint is identical whether a hash was reused or recomputed; a changed file is rehashed. Every capture site benefits: message submission, evidence at the start and end of native commands, review completion, evidence pages, shared-work claims and the Task evidence window. | `ProjectSnapshot.cs` |
| A provider over its limit failed the whole run | A provider reply that means "not now" (usage limit, rate limit, too many requests, credits) makes that participant sit out the rest of the phase: its opportunities are withdrawn, the user sees "… is unavailable for this phase" and the stream records it, and the other agent continues alone. A request addressed to the unavailable peer is reported as not delivered. When both providers are unavailable the run still pauses as before. | `ProviderLimits.cs`, `HubCoordinator.cs` |
| "yooo" ran the full two-agent pipeline | Casual greetings, thanks and acknowledgements (yo, sup, what's up, good night, gm, lol, thx, nice work, ok thanks and the earlier forms) are recognised as social: one agent replies and no preparation session is started. Words that can answer a question (yes, no, sure, go ahead) are deliberately not social. | `CollaborationGuard.cs`, `HubCoordinator.cs` |

## Verification

- Build: zero warnings, zero errors.
- Full regression suite: **221 tests passed**: the 217 of 0.16.0 plus four new cases in `HardeningTests`: a warm capture
  rehashes nothing and keeps the fingerprint, and a changed file is rehashed and detected (the existing same-size,
  restored-modification-time case still detects the edit through the change time); a provider over its limit sits out
  the phase while the other agent answers, with the user, the stream and the outcome told; both providers over their
  limits still pause the run; casual greetings are recognised, instructions are not, and a greeting runs one agent with
  no preparation session.
- Package `artifacts\responsiveness-017-release` (file version 0.17.0.0) passed `tests\Local-Diagnostics-Smoke.ps1` and
  `tests\Conversation-Management-Smoke.ps1` (all four conversation-management cases).
- Expected effect on the measured conversation: the first capture after the app starts still hashes the workspace once
  (about 30 seconds there); every later capture costs a listing and a stat per file. A greeting runs one agent turn.
  Codex over its weekly limit no longer stops Claude from answering; Codex's limit resets on October 3, 2026.
- Live provider check: not run; the Codex account is over its limit until October 3, 2026.
- Clean checkout of commit `ab158b2` at a short temporary path built with zero warnings and passed all 221 tests
  against its own packaged bridge.
- Installed on September 28, 2026 after the idle 0.16.0 app was closed (no owned or running tasks, no child
  processes): `artifacts\responsiveness-017-install-result.json` (611 files verified, package hashes match, 10 profile
  files verified, production data unchanged, backups `before-collaboration-20260928-172444.zip` and
  `before-collaboration-data-20260928-172444.zip`). Reopened as 0.17.0.0 with 4 rooms and 1 task intact:
  `artifacts\responsiveness-017-reopen-result.json`.
