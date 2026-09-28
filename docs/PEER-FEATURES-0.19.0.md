# Peer features, batch 2 — 0.19.0

The second batch from `docs/TODO.md`: addressing and handles.

| Feature | From | What it does here | Where |
| --- | --- | --- | --- |
| @mention addressing | OpenAgents Workspace | `@claude`, `@claude-code` or `@codex` anywhere in a message addresses that agent; the first mention speaks first. A message that mentions both with different asks ("@claude write the tests, @codex review the parser") gives each agent its own part as its assignment, tells it the teammate has the other part, and always reaches both, even for a request the scheduler would otherwise treat as simple. Leading and trailing forms ("Claude, …", "…, Codex?") still work. | `ConversationTurns.Mentions`, `SplitAsks`, `HubCoordinator` |
| Resume handles | official Codex plugin | The Tasks window shows each room's native Codex thread and Claude session ids with the exact commands (`codex resume <id>`, `claude --resume <id>`) to reopen the same thread in the CLI. | `MainWindow.Tasks` |
| Adversarial review preset | official Codex plugin | A fourth packaged workflow, `adversarial-review`: challenge the work, probe boundaries and error paths, report concrete failure scenarios, dispute wrongly addressed findings, fix nothing. Selected per review by putting "adversarial" in the `review_request` scope.focus, or by the user asking for an adversarial review. | `plugins/ai-hub-collaboration/skills/adversarial-review`, instructions |
| Per-phase usage totals | rennerdo30/agent-bridge | The providers' own usage reports are recorded per phase and agent in the ledger: Codex's cumulative per-thread totals become per-turn deltas; Claude's per-turn tokens and cumulative session cost likewise. The Tasks window and the review packet show this phase and all phases per agent. | `ProviderUsage`, `CollaborationStore.RecordUsage`, `CollaborationPresentation.UsageSummary` |

## Verification

- Build: zero warnings, zero errors.
- Full regression suite: **233 tests passed**: the 229 of 0.18.0 plus four new cases in `HardeningTests`: inline and
  hyphenated mentions recognised and e-mail addresses ignored, split asks parsed and each agent given its own part with
  the first mentioned speaking first; both providers' real usage payloads parsed and aggregated per phase and agent
  with the summary text; cumulative Codex totals recorded as one turn's delta through the coordinator; the adversarial
  workflow loaded with the plugin and referenced from the instructions.
- Package `artifacts\peer-features-019-release` (file version 0.19.0.0) passed `tests\Local-Diagnostics-Smoke.ps1` and
  `tests\Conversation-Management-Smoke.ps1` (all four conversation-management cases).
- Live provider check: not run; the Codex account is over its limit until October 3, 2026.
- PENDING: clean checkout verification, installation.
