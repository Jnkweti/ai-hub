# Decisions recorded on October 3, 2026

The learning plan's Phase 6 asks for decisions supported by evidence, not for work. These are the decisions the
evidence to date supports, with what would reopen each.

## Owning the tool execution loop: no, for now

The plan allowed a custom execution runtime "only if an observed limitation requires one". Across twelve pilot runs on
three tasks the observed execution limitations were two: Claude Code in plan mode cannot execute, and Codex's
read-only sandbox cannot run pytest because it cannot create temp files. The first was handled inside the existing
provider boundary — a read-only session now routes a needed command to its teammate as a `question` and gets captured
output back (0.28.3; seen live twice). The second is a provider sandbox property, not something a host-owned runtime
would change without taking on the providers' permission model. No pilot produced a wrong answer that execution
ownership would have prevented. Decision: keep the adapter boundary and the providers' native tool loops. Reopen if a
task class appears where neither agent can execute what the answer needs, or if a provider's sandbox blocks the
reproduction step more often than routing can absorb.

## Sequential reinforcement learning or parameter training: not now

There is no outcome data yet beyond the pilots, and the pilots do not separate the strategies on correctness. The
bounded first step the plan prescribes — a stated policy in shadow mode, limited exploration on designated evaluation
tasks, and a per-strategy report — is in place (0.32.0 to 0.34.0) and depends on the developer's feedback to say
anything. Decision: no sequential learner and no parameter training until the Strategy report shows both strategies
with enough judgements to differ, and even then the plan's comparison against fixed strategies comes first. Nothing
here trains or changes the providers' models.

## What the harness has shown

A contribution, a focused peer question, an evidence-backed answer and a recorded decision ran end to end with real
providers on real tasks without prescribed tool calls, twice; the ledger makes the chain inspectable without the
transcript; the developer could stop or redirect at every turn. That is the Phase 1 acceptance. What it has not shown
is improved outcomes: on the tasks tried, each provider alone was also right. The remaining evidence has to come from
the developer's own work and feedback, which the app now records.
