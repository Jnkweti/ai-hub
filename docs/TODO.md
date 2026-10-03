# AI Hub — TODO

Maintained by hand. Each item names where the idea came from, what it becomes in AI Hub, and a rough size
(S = a day or less, M = a few days, L = its own release). Check items off in the commit that ships them.

## Collaboration and preference learning roadmap

From the developer's September 29, 2026 discussion of emergent collaboration and learning from preferences; sequence revised October 3 to build the harness first. The [detailed plan](EMERGENCE-AND-LEARNING-PLAN.md) defines tasks, dependencies, and acceptance criteria. These milestones are proposed and unestimated; size individual implementation slices after mapping the current architecture.

- [x] **Phase 0: Pilot baseline.** Done October 3, 2026 (PILOT-BASELINE-0.28.0.md). Capture a small starting point while designing the harness; expand evaluation after its first working loop.
- [x] **Phase 1: Working collaboration harness.** Done as 0.28.0 to 0.28.4 (CHALLENGE-RESOLUTION-0.28.0.md onward); the full cycle ran live and unprescribed on October 3, 2026. Build the minimal host-owned coordination core and full contribution, challenge, revision, and developer-control path on existing provider clients.
- [ ] **Phase 2: Harness evaluation.** Items 1-5 done October 3, 2026; one answers-then-synthesis run done; selectable strategies shipped as 0.31.0; remaining: task-fitted contribution selection and a task hard enough to produce a wrong first answer. Exercise the loop, verify safeguards and live providers, refine reciprocal behavior, and compare results and cost.
- [x] **Phase 3: Feedback and outcomes.** Shipped as 0.29.0 (`FEEDBACK-0.29.0.md`): explicit, scoped, linked feedback with local inspect/edit/delete/export; comparisons between alternatives are limited to naming a preferred message. Capture attributed, scoped feedback and delayed outcomes with developer controls.
- [ ] **Phase 4: Preference memory.** Mostly shipped as 0.30.0 (`PREFERENCES-0.30.0.md`): scoped, versioned, inspectable preferences supplied below instructions and recorded per input; the reserved-task evaluation remains. Apply relevant, inspectable preferences while preserving explicit instructions and independent judgment.
- [ ] **Phase 5: Learned strategy selection.** Prerequisites now exist: two selectable strategies (0.31.0), feedback (0.29.0) and preferences (0.30.0); shadow mode is the next step. Evaluate a bounded learner against fixed policies and preference memory alone; promote only with evidence.
- [ ] **Phase 6: Deeper training decision.** Assess sequential reinforcement learning, model parameter training, and execution ownership as separate optional branches.

First implementation slice (done October 3, 2026): build the minimal working harness and its contribution, challenge, revision, and developer-control loop. Capture a lightweight baseline alongside implementation; add feedback and preference learning after the harness works.

## Batch 1 — responsiveness and safety (shipped as 0.18.0, see `PEER-FEATURES-0.18.0.md`)

- [x] **Per-turn inactivity watchdog** (from AgentBridge). A turn with no provider events for N seconds (default 300)
  is cancelled and the phase pauses with "no activity for N seconds", instead of hanging until Stop. Adapters'
  `SendAsync`; setting for N. S.
- [x] **Quota-aware scheduling** (from agent-quota-guard). Parse the reset time from a limit message ("try again at
  …"), store "unavailable until" per provider, skip dispatching to it until then without starting a process, show a
  countdown on the agent card, clear automatically. Extends the 0.17.0 sidelining. S.
- [x] **Stream event tiering** (from AgentBridge's IMPORTANT / STATUS / FYI). In delta prompts, user messages and peer
  conclusions arrive in full, tool completions are batched into counts, noise is omitted. `LiveStream.EventsSince`. S.
- [x] **Edit collision detection** (from hcom, Concord). When both residents touch the same file within a phase
  (Edit/Write and fileChange tool events already carry paths), post a "collision" stream event and a status to both.
  `CollaborationEvidence.Observe`. S.
- [x] **Phase completion gate** (from agent-bridge-mesh's export gate). At phase end, summarise unanswered peer
  questions, open or stale findings and undelivered requests as a system event and in the outcome reason, instead of
  "everyone passed". `HubCoordinator` phase end. S.
- [x] **Disputed findings** (from agent-bridge-mesh's disagreement records). A `disputed` finding disposition so a
  reviewer can record disagreement instead of passing quietly; disputes stay in the common context until resolved.
  Contract + `ValidateEvidenceAndReview` + `TaskContextBuilder.Build`. S.
- [x] **Review packet export** (from Concord's REVIEW_PACKET.md and CCB's shared memory file). Generate a per-task
  markdown packet (scope, evidence, findings, freshness, decisions, undelivered requests) from the ledger; offer it
  from the Tasks window and the Export button. `CollaborationPresentation`. S.

## Batch 2 — addressing and handles (shipped as 0.19.0, see `PEER-FEATURES-0.19.0.md`)

- [x] **@mention addressing** (from OpenAgents Workspace). Accept `@claude` and `@codex` anywhere in a message; two
  mentions with different asks become a split with two assignments. `ConversationTurns.AddressedSpeaker`,
  `CollaborationScheduler`. S to M.
- [x] **Resume handles** (from the official Codex plugin's `/codex:transfer`). Show each resident session's id and the
  exact resume command in the Tasks window so the same thread can be opened in the Codex or Claude TUI. S.
- [x] **Adversarial review preset** (from `/codex:adversarial-review`). A workflow instruction that steers a review to
  challenge assumptions, selectable per review request. `plugins/ai-hub-collaboration`. S.
- [x] **Per-phase usage totals**. Sum the Usage events per provider per phase and show them in the Tasks window. S.

## Batch 3 — bigger changes, one release each (shipped as 0.20.0, 0.21.0 and 0.22.0)

- [x] **Mid-turn push** (from AgentBridge, rennerdo30/agent-bridge, hcom). Shipped as 0.20.0, see `MIDTURN-PUSH-0.20.0.md`. Deliver new stream events into Claude's
  running turn through the bridge as MCP notifications; Codex keeps queueing to the next turn boundary. Claude's
  channel feature is a research preview behind a flag, so ship behind a setting. M to L.
- [x] **Bounded recovery loops** (from claude_codex_bridge). Shipped as 0.21.0, see `RECOVERY-0.21.0.md`. Restart a crashed provider process inside a phase with
  backoff (30 s to 30 min, circuit-break after six) and resume its session, instead of failing the run on the first
  crash. M.
- [x] **Per-agent worktrees** (from agent-bridge-mesh, rennerdo30 delegate). Shipped as 0.22.0, see `WORKTREES-0.22.0.md`. Optional isolated git worktree per agent
  for edit-enabled tasks; the host merges into an integration branch and shows conflicts explicitly. L.

## Carried over from the design review

- [x] **Per-room transcript files** (issue 10). Split `rooms.json` so a large room's history is not rewritten on every
  save. Shipped as 0.23.0, see `REMAINING-0.23.0.md`.
- [x] **Review freshness scoped to named files** (issue 2). A review stays fresh while the files it names are
  unchanged, even if unrelated files changed. Shipped as 0.23.0.

## Verification debt

- [x] Rerun `AIHub.Tests.exe --live-stream-live <fresh dir>` (0.14.0) and a Claude+Codex live pass for 0.15.0 to
  0.17.0 after the Codex limit resets on October 3, 2026 at 11:21 PM; record the results in the release docs. Done
  September 29, 2026 once Codex was usable again: both live checks passed on 0.24.0 (see `DEFAULT-MODEL-0.24.0.md`
  and `LIVE-STREAM-0.14.0.md`).
- [ ] Optional live runs with the experimental settings on: mid-turn push (a message while Claude Code speaks) and
  per-agent worktrees with the real CLIs in a git project.
- [x] Make the test runner report a thrown fixture exception as `FAIL <name>` instead of crashing with an unhandled
  exception (exit code -532462766). Shipped as 0.23.0.

## Deliberately not planned

- Concord-style `expected_version` locking: the host already serialises every ledger write.
- hcom-style agents spawning and forking each other; CCB-style tmux pane layouts: a different product shape.
- Replacing the evidence and snapshot-freshness model: none of the surveyed projects has an equivalent.

Sources: AgentBridge (raysonmeng/agent-bridge), agent-bridge-mesh (LEO0047), rennerdo30/agent-bridge, hcom
(aannoo), Concord MCP (Get-Concord-AI), claude_codex_bridge (trillllllll), openai/codex-plugin-cc, OpenAgents
Workspace. Survey done September 28, 2026.
