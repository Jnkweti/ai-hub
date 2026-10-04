# Evaluation tasks — 0.34.0

Phase 5's "limited exploration on designated evaluation tasks before ordinary tasks". The policy from 0.32.0 can now
act — but only where the developer says so, and with every choice recorded in a form that supports later comparison.

## What it does

| Piece | Behavior |
| --- | --- |
| Designation | Tasks window → "Mark as evaluation task" (and back). The flag is the developer's; nothing infers it. The task's details say so. |
| Setting | Settings → "Let the policy choose the strategy on evaluation tasks", off by default. With it off, an evaluation task behaves like any other. |
| Choice | On a two-agent phase of an evaluation task (not addressed to one agent), the coordinator asks the policy instead of recording a shadow suggestion. `StrategyAdvisor.Choose` executes the suggestion with probability 0.75 and the other strategy otherwise, so both strategies are seen under comparable conditions; a single-agent phase never explores. |
| Record | The decision goes to `strategy-shadow.json` in evaluation mode with the context, the suggestion and its reason, the executed strategy, the probability of the suggestion, whether it explored, and the policy version; the phase-start stream event names the chosen strategy and the decision. The shadow log summary and the Strategy report count evaluation phases separately. |
| Safety | Ordinary tasks never reach the chooser. A chooser failure is reported on the agent card and the configured strategy runs. Unmarking a task restores the configured strategy from its next phase. |

## Verification

- Build: zero warnings, zero errors. Full regression suite: **277 tests passed** — two new cases: the chooser
  executes the suggestion on a high draw and the alternative on a low draw, with the probability and the explored flag
  recorded, and never explores for one participant; an ordinary task with the chooser wired is never consulted, an
  evaluation task marked before its first phase runs the policy's choice (independent answers, three turns, a synthesis
  assignment, the phase event naming the choice), the decision is recorded in evaluation mode at phase 1 with
  p=0.75, the shadow hook is not consulted beside the chooser, the flag persists across a reload, and unmarking restores
  the configured strategy.
- No UI check was run; the Tasks window button and the Settings checkbox follow the existing patterns.
- Package `artifacts\evaluation-0340-release` (0.34.0.0) installed on October 3, 2026
  (`artifacts\evaluation-0340-install-result.json`: 612 files verified, hashes match, data unchanged, backups
  `before-collaboration-20261003-203718.zip` and `before-collaboration-data-20261003-203718.zip`); the app was left
  closed and no window was opened.
