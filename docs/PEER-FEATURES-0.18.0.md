# Peer features, batch 1 — 0.18.0

The first batch from `docs/TODO.md`: seven ideas borrowed from the September 28, 2026 survey of peer projects, each
adapted to AI Hub's ledger, contract and evidence rules.

| Feature | From | What it does here | Where |
| --- | --- | --- | --- |
| Inactivity watchdog | AgentBridge | A turn with no provider events for N seconds (default 300, Settings: 30–3600) is stopped and the conversation pauses with "produced no output for N seconds". Waiting for your approval or answer does not count. Audit code `TurnInactivity`. | `HubCoordinator.SendOneAsync`, `SettingsWindow` |
| Quota-aware scheduling | agent-quota-guard | The reset moment is parsed from a limit message ("try again at Oct 3rd, 2026 11:21 PM", "resets 3pm (America/New_York)", "in 20 minutes"; otherwise 5 minutes for a rate limit, an hour for a usage limit) and remembered in settings. A provider still over its limit is not dispatched to and starts no process; the other agent continues alone; a single recipient over its limit is refused before sending with the reset time. The agent card shows "Unavailable · until …" with the time left and clears itself. | `ProviderLimits.UnavailableUntil`, `HubCoordinator`, `MainWindow` |
| Stream event tiering | AgentBridge | In delta prompts, user and peer entries arrive in full, a run of finished tool calls becomes one summary line with an error count, and a quiet pass is one line. Agents page details with `get_events` and `get_evidence`. | `LiveStream.EventsSince` |
| Edit collision detection | hcom, Concord | Native edit events (Claude Edit/Write, Codex completed fileChange) are recorded per phase. The second agent to change a file another agent already changed is told once per file, and a system event tells both to re-read and say which change stands. | `CollaborationEvidence.RecordTouches`, `CollaborationDocument.Touches` |
| Phase completion gate | agent-bridge-mesh | When a phase ends, the host names what is outstanding: unanswered peer requests (handoff, review request, question), open findings, addressed findings awaiting a peer check, disputed findings. It goes into the stream, the activity feed and the task outcome. | `CollaborationStore.PhaseSummary` |
| Disputed findings | agent-bridge-mesh | A reviewer returns an existing finding with disposition `disputed` and the reason, instead of passing quietly. A disputed finding blocks `assignment_complete` until a later review resolves it. | `CollaborationContract`, `ValidateEvidenceAndReview`, instructions |
| Review packet | Concord, claude_codex_bridge | Tasks → Review packet writes a markdown file: objective and instructions, this phase's assignments, findings by disposition with freshness, reviews, unanswered requests, shared checks, evidence with freshness, provenance. Everything attributed; nothing certified. | `CollaborationPresentation.ReviewPacket`, `MainWindow.Tasks` |

## Verification

- Build: zero warnings, zero errors.
- Full regression suite: **229 tests passed**: the 221 of 0.17.0 plus seven new cases in `HardeningTests` (a silent
  turn stopped by the watchdog while a streaming turn and a turn waiting on the user are not; limit messages parsed to
  a reset moment, with cooldowns otherwise; a provider known to be over its limit skipped with the other agent
  answering, and both over their limits failing fast; tiered delta prompts; a collision told once and recorded once;
  the completion gate naming an unanswered request; the review packet's sections) and one in
  `CollaborationEvidenceTests` (a reviewer disputes a finding, and the dispute blocks completion). The published
  submission schema was updated for the new disposition.
- Package `artifacts\peer-features-018-release` (file version 0.18.0.0) passed `tests\Local-Diagnostics-Smoke.ps1` and
  `tests\Conversation-Management-Smoke.ps1` (all four conversation-management cases).
- Live provider check: not run; the Codex account is over its limit until October 3, 2026.
- PENDING: clean checkout verification, installation.
