# AI Hub — TODO

Maintained by hand. Each item names where the idea came from, what it becomes in AI Hub, and a rough size
(S = a day or less, M = a few days, L = its own release). Check items off in the commit that ships them.

## Batch 1 — responsiveness and safety (target: 0.18.0)

- [ ] **Per-turn inactivity watchdog** (from AgentBridge). A turn with no provider events for N seconds (default 300)
  is cancelled and the phase pauses with "no activity for N seconds", instead of hanging until Stop. Adapters'
  `SendAsync`; setting for N. S.
- [ ] **Quota-aware scheduling** (from agent-quota-guard). Parse the reset time from a limit message ("try again at
  …"), store "unavailable until" per provider, skip dispatching to it until then without starting a process, show a
  countdown on the agent card, clear automatically. Extends the 0.17.0 sidelining. S.
- [ ] **Stream event tiering** (from AgentBridge's IMPORTANT / STATUS / FYI). In delta prompts, user messages and peer
  conclusions arrive in full, tool completions are batched into counts, noise is omitted. `LiveStream.EventsSince`. S.
- [ ] **Edit collision detection** (from hcom, Concord). When both residents touch the same file within a phase
  (Edit/Write and fileChange tool events already carry paths), post a "collision" stream event and a status to both.
  `CollaborationEvidence.Observe`. S.
- [ ] **Phase completion gate** (from agent-bridge-mesh's export gate). At phase end, summarise unanswered peer
  questions, open or stale findings and undelivered requests as a system event and in the outcome reason, instead of
  "everyone passed". `HubCoordinator` phase end. S.
- [ ] **Disputed findings** (from agent-bridge-mesh's disagreement records). A `disputed` finding disposition so a
  reviewer can record disagreement instead of passing quietly; disputes stay in the common context until resolved.
  Contract + `ValidateEvidenceAndReview` + `TaskContextBuilder.Build`. S.
- [ ] **Review packet export** (from Concord's REVIEW_PACKET.md and CCB's shared memory file). Generate a per-task
  markdown packet (scope, evidence, findings, freshness, decisions, undelivered requests) from the ledger; offer it
  from the Tasks window and the Export button. `CollaborationPresentation`. S.

## Batch 2 — addressing and handles (target: 0.19.0)

- [ ] **@mention addressing** (from OpenAgents Workspace). Accept `@claude` and `@codex` anywhere in a message; two
  mentions with different asks become a split with two assignments. `ConversationTurns.AddressedSpeaker`,
  `CollaborationScheduler`. S to M.
- [ ] **Resume handles** (from the official Codex plugin's `/codex:transfer`). Show each resident session's id and the
  exact resume command in the Tasks window so the same thread can be opened in the Codex or Claude TUI. S.
- [ ] **Adversarial review preset** (from `/codex:adversarial-review`). A workflow instruction that steers a review to
  challenge assumptions, selectable per review request. `plugins/ai-hub-collaboration`. S.
- [ ] **Per-phase usage totals**. Sum the Usage events per provider per phase and show them in the Tasks window. S.

## Batch 3 — bigger changes, one release each

- [ ] **Mid-turn push** (from AgentBridge, rennerdo30/agent-bridge, hcom). Deliver new stream events into Claude's
  running turn through the bridge as MCP notifications; Codex keeps queueing to the next turn boundary. Claude's
  channel feature is a research preview behind a flag, so ship behind a setting. M to L.
- [ ] **Bounded recovery loops** (from claude_codex_bridge). Restart a crashed provider process inside a phase with
  backoff (30 s to 30 min, circuit-break after six) and resume its session, instead of failing the run on the first
  crash. M.
- [ ] **Per-agent worktrees** (from agent-bridge-mesh, rennerdo30 delegate). Optional isolated git worktree per agent
  for edit-enabled tasks; the host merges into an integration branch and shows conflicts explicitly. L.

## Carried over from the design review

- [ ] **Per-room transcript files** (issue 10). Split `rooms.json` so a large room's history is not rewritten on every
  save. M.
- [ ] **Review freshness scoped to named files** (issue 2). A review stays fresh while the files it names are
  unchanged, even if unrelated files changed. M.

## Verification debt

- [ ] Rerun `AIHub.Tests.exe --live-stream-live <fresh dir>` (0.14.0) and a Claude+Codex live pass for 0.15.0 to
  0.17.0 after the Codex limit resets on October 3, 2026 at 11:21 PM; record the results in the release docs.
- [ ] Make the test runner report a thrown fixture exception as `FAIL <name>` instead of crashing with an unhandled
  exception (exit code -532462766).

## Deliberately not planned

- Concord-style `expected_version` locking: the host already serialises every ledger write.
- hcom-style agents spawning and forking each other; CCB-style tmux pane layouts: a different product shape.
- Replacing the evidence and snapshot-freshness model: none of the surveyed projects has an equivalent.

Sources: AgentBridge (raysonmeng/agent-bridge), agent-bridge-mesh (LEO0047), rennerdo30/agent-bridge, hcom
(aannoo), Concord MCP (Get-Concord-AI), claude_codex_bridge (trillllllll), openai/codex-plugin-cc, OpenAgents
Workspace. Survey done September 28, 2026.
