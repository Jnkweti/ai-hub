# Shadow strategy advice — 0.32.0

Phase 5 of [EMERGENCE-AND-LEARNING-PLAN.md](EMERGENCE-AND-LEARNING-PLAN.md) begins the way the plan requires: the
action set, the context features and the outcomes are defined first, and the policy runs in shadow mode. Nothing it
suggests is executed, and nothing here is trained.

## What it does

| Piece | Behavior |
| --- | --- |
| Action set | Two strategies from 0.31.0: reaction rounds, and independent answers then synthesis. Explicit routing (addressed messages, split asks, simple requests) and permissions are outside the action set. |
| Context | `StrategyContext`: prompt length in words, whether the prompt asks for a judgement (diagnose, compare, decide, review…), whether it mentions code, whether this is the task's first phase, the participant count, and the counts of useful and needs-correction feedback recorded under each strategy. |
| Policy shadow-v1 | Deterministic and versioned. With one participant: reaction. Once both strategies have at least five feedback judgements and their useful rates differ by fifteen points or more, the better-judged strategy. Otherwise a stated heuristic: a first-phase judgement question of at least 25 words suggests independent answers; everything else suggests reaction rounds. |
| Record | Per two-agent phase the coordinator calls the advisor with the prompt, task, phase and the strategy about to run; the decision (context, suggested, executed, reason, policy version) is stored in `strategy-shadow.json` and a `Shadow strategy: …` system event enters the stream beside the phase-start event. A failure to record is reported on the agent card and ignored. |
| Outcome link | `FeedbackRecord.Strategy` now holds the strategy setting in force when the feedback was recorded, so later analysis can join judgements to strategies; the shadow log joins to phases by task id and generation. |
| Developer control | Settings → "Record shadow strategy suggestions" (on by default) stops recording without affecting collaboration. The Tasks window lists a task's shadow decisions and the overall agreement rate between the shadow policy and what ran. |

## What it is not

Not a learner: shadow-v1 is a heuristic whose only data-driven branch is a feedback-count comparison with a minimum
sample and a margin. No exploration, no execution of suggestions, no credit assignment, no claim that one strategy is
better. The plan's remaining Phase 5 items — limited exploration on designated evaluation tasks, team-level credit,
comparison against fixed strategies on reserved tasks, policy versioning with rollback — stay open and depend on
feedback actually accumulating.

## Verification

- Build: zero warnings, zero errors. Full regression suite: **274 tests passed** — three new cases: the policy is
  deterministic over the stated features (a substantial first-phase judgement question suggests independent answers; a
  later phase, a short ask and a single participant suggest reaction; feedback overrides the heuristic only once both
  strategies have enough judgements); each two-agent phase records a decision with its context, executed strategy
  and policy version, the stream carries the shadow line, a single-agent phase records nothing, and the log persists
  and summarizes; a damaged log is repaired and backed up.
- Package `artifacts\shadow-0320-release` (0.32.0.0) passed the three smoke checks; installed on October 3, 2026
  after closing the idle 0.31.0 app (`artifacts\shadow-0320-install-result.json`: 612 files verified, hashes match,
  data unchanged, backups `before-collaboration-20261003-184048.zip` and
  `before-collaboration-data-20261003-184048.zip`); reopened as 0.32.0.0 with 5 rooms and 2 tasks.
- Live: not run separately; the shadow line appears in the stream of every later two-agent phase, including the
  pilot runner's, which now wires the advisor the same way the desktop does.
