# AI Hub collaboration and preference learning plan

Status: Draft implementation roadmap, created September 29, 2026; revised October 3, 2026 to put harness development first. Unchecked items are proposed work, not implemented capabilities. Architecture choices below are proposals to validate during implementation.

Build within the current AI Hub repository toward the outcome in [PROJECT-VISION.md](../PROJECT-VISION.md): the developer, Claude, and Codex improve results through independent judgment, reciprocal influence, and complementary contributions. Add learning that helps the collaboration adapt to the developer's preferences while preserving useful differences between the models.

The first release should be a working collaboration harness inside AI Hub. It should support the full interaction loop: independent contributions, a useful challenge, evidence or a question, a revised contribution, and a developer-directed outcome. The existing native provider clients can initially execute model work. Preference memory and reinforcement learning come later, after the harness produces behavior we can observe and improve.

## Scope and existing foundations

Keep the desktop workspace, native provider clients, evidence and persistence infrastructure. Build the harness inside the existing solution, reusing and extending the current collaboration system. Introduce modular coordination and learning services as their boundaries become clear. A new repository is unnecessary.

| Existing component | Proposed use |
| --- | --- |
| `IAgentClient`, `CodexClient`, `ClaudeClient` | Keep provider execution behind the existing interface while experimenting with coordination. |
| `HubCoordinator`, `CollaborationScheduler` | Extract strategy decisions from execution and lifecycle handling. |
| `ConversationPreparation` | Extend the existing tentative independent preparation where useful; retain its tool restrictions. |
| `CollaborationStore`, `TaskContext`, `LiveStream`, `SharedWork` | Reuse attributed context, assignments, events, and shared work; extend schemas only where necessary. |
| `CollaborationEvidence`, `ContextInputManifest`, usage records | Link claims to observations and record the context, strategy, and resource use of each experiment. |
| Existing desktop questions, pins, approvals, Stop, exports | Integrate feedback and preference controls into established interactions. |

The existing [structured collaboration plan](COLLABORATION-IMPLEMENTATION-PLAN.md) describes earlier implementation. This roadmap extends that foundation; its completed milestones do not mean the work below is complete.

## Phase 0 Prepare a lightweight baseline alongside harness development

- [x] (0.28.0, October 3, 2026) Inventory current behavior and regression checks during the harness design. Check the current checkout and coordinate around concurrent edits.
- [x] (PILOT-BASELINE-0.28.0.md) Pick one representative pilot task and record a small starting-point comparison for current AI Hub and the solo providers where practical. Use the same initial task state and note model, settings, context, and resource use.
- [x] (PILOT-BASELINE-0.28.0.md, pre-registered rubric) Define what the harness must show: useful discoveries, evidence-backed challenges, revisions, correctness, unresolved questions, developer intervention, and task completion.
- [x] Keep baseline setup small enough that it does not delay the harness. Expand representative tasks, repeats, order controls, and comparable-resource analysis after the first working loop.

Acceptance: A pilot task and starting-point notes are ready while harness work proceeds. This early comparison is directional evidence, not proof that the new harness improves results.

## Phase 1 Build the minimal collaboration harness

- [x] (CHALLENGE-RESOLUTION-0.28.0.md) Map the current coordinator, scheduler, task ledger, provider clients, and collaboration message path. Extend existing capabilities where they fit; avoid building a second task store or parallel provider adapters.
- [x] (the existing HubCoordinator turn loop, extended rather than replaced) Establish a small harness core that owns the task objective and current instructions, agent turn scheduling, shared collaboration state, contribution exchange, stop conditions, and user control.
- [x] (Opportunity slots in HubCoordinator: plain reaction, delivered request, resolution) Define the policy boundary: given task context, selected agents, and constraints, choose the next collaboration action and its purpose. Keep permissions and tool authorization enforced by the host.
- [x] Keep Codex and Claude behind the existing provider-client interface initially. The harness should coordinate their work without requiring a custom model tool runtime.
- [x] (0.28.0; live and unprescribed in the sixth pilot run on 0.28.4) Complete one vertical slice: receive a developer task, collect distinct agent contributions, route a focused peer question or challenge, deliver the peer's response back for revision, then return a result with unresolved issues visible.
- [x] (esolution assignment with question and answer IDs, eply_to on the decision, stream events, input manifests) Persist attribution, message links, supplied context revision, evidence references, and run outcome so the developer can follow how the solution changed.
- [x] (259 regression tests) Preserve existing interruption, approval, provider failure, stale-dispatch, bounded-round, evidence, and edit-ownership safeguards throughout the new loop.
- [x] (automatic collaboration and voluntary follow-ups remain settings; a resolution turn is bounded by the same round cap) Keep the existing coordination policy as a selectable fallback during the transition. Experimental coordination must be easy to disable without losing history.

Acceptance: A real task can complete the full collaboration loop through AI Hub; the developer can redirect or stop it; provider access stays within host permissions; and the saved record identifies each contribution, challenge, revision, and outcome.

## Phase 2 Exercise and refine the harness

- [x] (three tasks, six arms, two routed runs in PILOT-BASELINE-0.28.0.md) Run the pilot task through the new harness and compare it with the Phase 0 starting point. Record quality, useful challenges and revisions, developer effort, time, and provider usage.
- [x] (ChallengeResolutionTests, ConcurrentWorkTests grace and wrapper cases, routing cases) Add repeatable checks for message attribution, evidence links, context delivery, revision history, cancellation, provider errors, round limits, and preservation of current permissions.
- [x] (--challenge-live, --pilot-live, --preparation-live) Run focused live verification with both providers. Identify differences between recorded messages and content actually supplied in provider inputs.
- [x] (0.28.1 preparation grace and restatement-is-a-pass, 0.28.2 solo note, 0.28.3 execution routing, 0.28.4 claim matching; endorsement turns remain a known gap) Refine the interaction when it repeats work, manufactures disagreement, hides unresolved issues, or continues after useful progress ends.
- [x] (solo Codex and solo Claude arms for every pilot task; one independent-answers-then-synthesis run on the second task) Expand the evaluation to independent solo runs and independent answers followed by synthesis. Compare under practical settings and, where feasible, similar resource limits.
- [x] (no fixed roles exist: the first speaker rotates per phase and so do the synthesizer and the research requester; either agent asks, answers, reviews or resolves as the exchange requires — pilots show Codex and Claude Code in every position) Select contributions to fit the task. Either model may investigate, implement, challenge, or synthesize; avoid permanent model roles.
- [x] (0.31.0: Settings → Collaboration strategy; reaction rounds or independent answers then synthesis, recorded per phase; no behavioral profiles) Keep independent assessment, optional behavioral profiles, and other experimental strategies as selectable policies. Describe profiles in observable terms; do not assume MBTI compatibility predicts performance.
- [x] (quiet passes, the completion gate and the resolution turn's visible retain/revise exist since 0.18.0 and 0.28.0) Retain the option for one agent to pass when the peer adds nothing useful. Show unresolved disagreements and let the developer redirect or settle a decision.

Acceptance: Repeated trials show the harness can preserve useful differences and trace a contribution through challenge and revision. Report results and resource costs honestly; one successful demonstration does not establish better intelligence or prove emergence.

## Phase 3 Capture feedback and outcomes

- [x] (0.29.0: feedback button on agent messages, Feedback in the Tasks window) Add lightweight feedback on a contribution or task: useful, needs correction, preferred alternative, and an optional explanation. Support comparisons only when alternatives actually exist.
- [x] (0.29.0: `FeedbackRecord` links message, dispatch, ledger message and task IDs, the strategy fingerprint and the outcome; `feedback.json` is versioned and repaired on load) Link feedback to the relevant contribution, task, strategy version, and outcome. Version new storage schemas and verify migration without losing existing task history. Record explicit feedback separately from inferred signals such as accepted edits or later corrections.
- [x] (0.29.0: optional dimensions correctness, relevance, explanation, scope, initiative, collaboration, effort; approvals are not feedback) Separate dimensions: correctness, relevance, explanation style, scope, initiative, collaboration value, and developer effort. A permission approval is not a quality rating.
- [x] (0.29.0: a record can be edited later and keeps its links; the outcome at recording time is kept; no feedback means unknown) Keep delayed outcomes and later corrections linked to the original work. No feedback means unknown, rather than success or failure.
- [x] (0.29.0: scope chosen in the dialog, default this task) Let the developer decide whether feedback applies only to this task, this project, a task category, or generally. Avoid interpreting every conversational remark as a global preference.
- [x] (0.29.0: All feedback window; the judged text is hashed, not copied) Store learning records locally with inspect, edit, delete, and export controls. Exclude masked/private answers and avoid copying sensitive transcript content into training records by default.

Acceptance: Feedback survives restart and can be traced to its source. The developer can correct or remove it, and the system does not turn silence or permission decisions into rewards.

## Phase 4 Apply inspectable preference memory

- [x] (0.30.0: `PreferenceRecord` with scope, supporting feedback ids, confidence, dates, enabled/disabled reason and a version per edit; nothing is inferred, so every record is developer-confirmed) Store preferences with their scope, supporting feedback, confidence, date, and superseded state. Inferred preferences remain visibly tentative.
- [x] (0.30.0: `PreferenceStore.Relevant` by general, project and task scope; the common context lists them and input manifests record `pref:<id>:v<n>`) Retrieve only relevant preferences into the task context and record which preference versions were supplied to each agent.
- [x] (0.30.0: the preference block sits below ACTIVE USER INSTRUCTIONS and states its lower precedence; a preference is created only when the developer writes or confirms it) Give current explicit instructions precedence over learned preferences. Resolve conflicts explicitly; do not silently turn a one-time correction into a permanent rule.
- [x] (0.30.0: Preferences window from the Tasks window; a preference made from feedback is disabled when that feedback is deleted; the window says running or resumed sessions may retain earlier context) Provide a preference view with editing, disabling, and deletion. Invalidate derived preferences when their supporting feedback is removed, or retain them only when other evidence supports them. Removing a preference affects future inputs; explain that already-running or resumed native sessions may retain earlier context.
- [x] (0.30.0: preferences are developer preferences supplied to both agents alike; no per-agent profiles exist) Keep shared developer preferences separate from optional per-agent behavioral experiments. Both agents can honor the same goal while contributing differently.
- [ ] (0.30.0: one in-scope versus out-of-scope live pair on the pilot task; the reserved-task comparison remains) Evaluate against collaboration without preference memory, using reserved tasks and checking that useful disagreement and verified correctness are preserved.

Acceptance: A developer preference changes behavior on a relevant later task, does not leak into unrelated tasks, and can be overridden or disabled. This is persistent adaptation through context, not model parameter training or reinforcement learning.

## Phase 5 Test learning to choose collaboration strategies

Start with a small strategy selection experiment. A candidate contextual bandit chooses among eligible strategies from task context and updates its choices from feedback. This is a proposed first learning algorithm, not a commitment to a particular implementation.

- [x] (0.32.0: two actions, reaction rounds and independent answers then synthesis; explicit routing and permissions untouched) Define a small, bounded action set from validated Phase 2 strategies, such as direct contribution with a focused peer check, independent alternatives followed by comparison, or investigation followed by review. Preserve explicit user routing and permissions.
- [x] (0.32.0: `StrategyContext` — prompt words, judgement and code signals, first phase, participants, per-strategy feedback counts; outcomes are the feedback kinds per strategy, reported separately from cost) Define context features and outcomes before training. Keep preference satisfaction, verified correctness, developer effort, and resource use separately reportable.
- [x] (0.32.0: `StrategyAdvisor` policy shadow-v1, deterministic and versioned, recorded per two-agent phase in `strategy-shadow.json` and the stream beside the executed strategy; a setting disables recording) Begin in shadow mode: record the learner's suggestion while the established policy executes. Shadow suggestions do not establish how the unexecuted strategy would have performed.
- [x] (0.34.0: a task the developer marks as an evaluation task lets the policy choose, exploring the alternative a quarter of the time; the decision records the action set, the probability of the suggestion, the policy version and whether it explored; a setting gates it, off by default; ordinary tasks never reach it) Test limited exploration on designated evaluation tasks before ordinary tasks. Record available actions, selection probabilities, policy versions, and delayed feedback for valid comparisons.
- [ ] (choices are team-level strategies; no per-agent credit exists) Learn at the team strategy level first. Retain contribution attribution, but do not assign causal credit to an agent solely because it spoke last or received praise.
- [ ] (0.33.0: the Strategy report in the Tasks window gives per-strategy feedback counts, aspects, shadow agreement and disagreements with caveats; the reserved-task comparison itself remains) Compare the learned policy with fixed strategies, current collaboration, and preference memory alone on reserved tasks. Report uncertainty and tradeoffs rather than only an aggregate reward.
- [ ] (shadow-v1 is versioned in every decision; rollback is moot while nothing executes) Version policy updates and support rollback. Let the developer disable learning without disabling collaboration or explicit preferences.

Acceptance: Measured strategy selection improves the predefined outcomes without sacrificing correctness or useful diversity. If evidence is insufficient, retain the fixed policy and keep collecting interpretable feedback.

## Phase 6 Decide whether deeper training is justified

- [ ] Evaluate whether strategy selection is enough or whether sequences of collaboration decisions need a longer-term reinforcement learning policy.
- [ ] Identify data requirements, delayed rewards, exploration limits, and evaluation methods before selecting a sequential learning algorithm.
- [ ] Consider model parameter training separately, only if a suitable trainable model and a reviewed dataset are available. Harness learning does not modify the parameters of the current native provider models.
- [ ] If parameter training proceeds, separate training and evaluation data, version datasets and models, and compare with the same harness using unchanged models.
- [ ] Decide whether owning the full tool execution loop would unlock a measured need. Preserve the adapter boundary if a custom execution runtime is introduced.

Acceptance: Record a decision supported by evidence. Deeper reinforcement learning, parameter training, and a custom execution runtime are optional branches, not prerequisites for the project vision.

## First implementation slice: working harness

Complete Phases 0 to 2 before adding preference memory or a learning policy:

1. Choose a pilot task and record a lightweight starting point while reviewing current coordination behavior.
2. Add or refactor the harness core and policy boundary inside AI Hub, using existing provider clients.
3. Complete a contribution, peer challenge or question, revision, and developer-controlled finish.
4. Persist the attributed interaction and verify stopping, evidence, and permission behavior.
5. Compare the pilot with the starting point; refine the loop before expanding preference learning.

Deliverables: the first working harness path, visible developer controls, a traceable collaboration record, focused regression checks, live provider verification, and a short pilot comparison. This establishes that the harness works; broader repeated evaluation is required before claiming effectiveness.

## Implementation and verification practice

Before each slice, recheck current changes and coordinate file ownership when another implementer is active. Preserve unrelated edits. Check milestones off only when their acceptance criteria are met and evidence is recorded.

Use deterministic fixtures for policy decisions, feedback lifecycle, preference scope and precedence, migration, cancellation, and stale dispatch handling. Run the existing checks affected by orchestration changes, then verify the relevant behavior with both real providers. Human evaluation assesses usefulness; passing software tests alone cannot demonstrate emergence or successful personalization.

Do not optimize for agreement, praise, message volume, or simulated personality. Reward useful challenges and better outcomes; record failures and disagreements rather than hiding them behind a completion status.

## Research informing the learning options

[Learning from human preferences](https://arxiv.org/abs/1706.03741) demonstrates training from comparisons in other environments. [Contextual bandit research](https://arxiv.org/abs/1003.0146) demonstrates adapting choices from contextual feedback. These inform possible mechanisms; neither establishes effectiveness for AI Hub or proves emergent collaborative intelligence.
