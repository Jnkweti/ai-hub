# Handoff for Claude: build the AI Hub collaboration harness

Prepared October 3, 2026. This handoff turns the developer's project vision and harness-first decision into the next implementation task. Begin by inspecting the current checkout and preserving its in-progress changes, then implement the smallest complete harness capability that materially advances the vision.

## Assignment

Extend AI Hub into an environment where the developer, Claude, and Codex can combine their independent judgment into better task outcomes. Start by completing and evaluating a working collaboration harness inside this repository. Preference learning and reinforcement learning are later stages; do not make them prerequisites for the harness.

The governing product intent is [PROJECT-VISION.md](PROJECT-VISION.md). Its main requirements are independent judgment, complementary and changing contributions, reciprocal influence, useful visible disagreement, and developer control. Treat useful discoveries, better verified outcomes, and reduced developer correction effort as measures of progress. Agreement, longer transcripts, and more agent calls do not demonstrate success by themselves.

This is an implementation handoff. Inspect first, then proceed into a focused implementation slice and verify it. Do not stop after returning an architecture survey or another proposal unless an actual blocker prevents safe progress.

## What “build the harness” means here

AI Hub is an existing WPF and .NET collaboration product, not an empty repository. It already has provider adapters and substantial coordination infrastructure. Build on and improve those foundations; do not create a second application, provider adapter stack, or task ledger.

For the first slice, the harness is the shared layer that owns a task's active developer instructions, chooses who contributes next and why, carries shared evidence and contributions between participants, records changes in understanding, and keeps the developer in control. Initially it can invoke the existing Codex and Claude clients, which run their providers' native tool loops. A custom execution runtime is a separate decision to make only if an observed limitation requires one.

The target interaction is a complete cycle: the agents can form distinct assessments, exchange a consequential question or challenge, use evidence to revise or retain their views, and give the developer a result with any unresolved decision visible. The scheduler should select the useful next move for the task. It should allow a model to pass when it has nothing useful to add, rather than requiring identical work or a fixed “implementer/reviewer” identity every time.

## Current repository foundations to inspect

- `IAgentClient`, `CodexClient`, and `ClaudeClient` provide the existing provider boundary.
- `HubCoordinator` currently manages task runs, agent turns, shared context, user interjections, structured collaboration, stopping, and turn lifecycle. Read the actual loop before deciding what must change.
- `CollaborationScheduler` and `CollaborationStore` support coordination policies, persisted tasks, structured messages, and evidence-linked activity.
- `TaskContext`, `LiveStream`, `SharedWork`, `CollaborationEvidence`, and input manifests support attributed context, shared work, host-captured evidence, and recording what context an agent received.
- `ConversationPreparation` already supports a restricted, provisional peer contribution which can later be revised. Preserve its tool restrictions.
- The desktop configures separate native, collaboration-enabled, preparation, and read-only provider clients in `src/AIHub.Desktop/MainWindow.xaml.cs`.
- [docs/COLLABORATION-IMPLEMENTATION-PLAN.md](docs/COLLABORATION-IMPLEMENTATION-PLAN.md) describes an earlier structured-collaboration implementation. Its header reports its original six milestones as complete in version 0.7.0. Use the current source and tests to establish what exists today; do not mistake that historical status for a description of the present feature gaps.

The current collaboration is already more than a basic two-agent chat. Identify the precise remaining gap between its actual behavior and the target interaction before adding code. Prefer a small behavior change that creates and records meaningful reciprocal revision over a broad “harness” refactor that only renames existing responsibilities.

## Required work sequence

1. **Inspect the checkout.** Check `git status`, recent history, relevant source, tests, `PROJECT-VISION.md`, and [the current roadmap](docs/EMERGENCE-AND-LEARNING-PLAN.md). Identify edits already in progress and coordinate around them. Do not discard, reset, or overwrite uncommitted work.
2. **Map actual behavior.** Follow one user task through provider dispatch, initial contribution, peer message or question, context delivery, later agent turn, user intervention, persistence, and completion. Confirm what the models actually receive and what the UI/export records. Report confirmed capability separately from the gap you find.
3. **Choose a focused vertical slice.** Keep baseline coordination as a fallback. Make one task-relevant reciprocal exchange observable and traceable. Refactor the coordinator only where required to give that behavior a clear seam; avoid rewriting native provider execution.
4. **Implement the slice.** Preserve current schemas and stored tasks where possible. If a persistence change is needed, use a versioned, backward-compatible path. Preserve edit ownership, provider permissions, approvals, cancellation, stop, round limits, stale-dispatch checks, and evidence provenance.
5. **Verify the behavior.** Add or update focused tests for the changed behavior and affected failure paths. Build and run the relevant existing checks. If provider access is configured and live verification is safe, exercise the path with both providers; label fixtures and live results distinctly.
6. **Report the result.** State what changed, what already existed, what new behavior the vertical slice adds, files touched, checks actually run, live verification performed or unavailable, known limitations, and the next milestone. Do not claim measured emergence or quality improvement from a single demonstration.

## Definition of done for the first slice

- AI Hub completes at least one developer task through an observable cycle of contributions, a focused peer challenge or question, and a response that considers that peer input.
- Each contribution and revision has an author and clear relationship to the triggering message or evidence. The stored context/input record makes it possible to distinguish a message the hub saved from content the provider actually received.
- The developer can redirect or stop the run, and existing host permission boundaries still govern agent actions.
- A quiet pass, unavailable provider, cancellation, or unresolved disagreement ends in an honest visible state; none is reported as verified completion.
- Regression checks protect task isolation, evidence validation, round and resource bounds, and the fallback path.
- The developer can inspect what happened without needing to infer the agents' interaction from an undifferentiated transcript.

A successful first slice demonstrates that the harness mechanism functions. Broader repeated comparisons are still required to determine whether collaboration improves results compared with solo work or simple answer synthesis.

## Evaluation and later learning

Use a lightweight pilot baseline while building: capture current AI Hub behavior and, where practical, solo Codex and solo Claude runs from the same initial task state. Record model, settings, supplied context, tool evidence, developer corrections, elapsed time, and resource use when available. Expand to repeated tasks and independent-answer-plus-synthesis comparisons after the first working loop. Do not delay the harness for an elaborate benchmark.

After the harness is working, the roadmap stages the learning work as follows:

1. Capture explicit feedback linked to the relevant contribution, task, strategy, and outcome. Keep inferred signals separate; absence of feedback means unknown.
2. Add local, inspectable preferences with scope, provenance, correction, disable, and delete controls. Current developer instructions take precedence; a one-time correction must not silently become a global preference.
3. Only after these foundations, evaluate a bounded learner that selects among collaboration strategies. Compare it with fixed strategies and preference memory alone; preserve useful diversity and correctness.
4. Consider longer-horizon reinforcement learning, training model parameters, or owning provider tool execution only as separate evidence-led choices.

The detailed acceptance criteria are in [docs/EMERGENCE-AND-LEARNING-PLAN.md](docs/EMERGENCE-AND-LEARNING-PLAN.md) and the phase checklist is in [docs/TODO.md](docs/TODO.md). Keep those documents in sync if implementation changes the sequence.

## Checkout state and safe collaboration

At handoff preparation, branch `main` points to commit `6eac301` (`docs: record 0.27.0 clean checkout verification and installation`). The worktree is dirty. The following paths are already modified or untracked; ownership is not established by Git, so inspect and preserve their contents before editing or staging:

- `src/AIHub.Core/LocalStore.cs`, `src/AIHub.Desktop/MainWindow.xaml`, `src/AIHub.Desktop/MainWindow.xaml.cs`, `tests/AIHub.Tests/ActivityFeedTests.cs`, and `README.md` include work to make **Open full log** open a disposable snapshot instead of the live activity file. The snapshot is meant to preserve diagnostics while edits or deletion of the copy leave the recording intact.
- `PROJECT-VISION.md` records the developer-confirmed project purpose. `DESIGN.md` and `README.md` link to it.
- `docs/EMERGENCE-AND-LEARNING-PLAN.md` and `docs/TODO.md` record the proposed harness-first development sequence. This handoff file is new.

The snapshot change and planning work are outside the harness implementation unless a direct dependency requires them. Preserve their edits. Do not stage or commit unrelated changes, publish an installation, or terminate a running app as part of this slice. If a separate worktree is needed to avoid collisions, first make sure it does not lose access to the actual in-progress state.

The activity snapshot implementation was built and its focused snapshot tests were reported passing in an isolated output directory in the earlier Codex session. That historical result was not rerun while preparing this handoff; verify the current checkout before relying on it. The current handoff task itself has not run a build or tests.

## First response to the developer

After inspecting the tree, give a brief status that names the current collaboration behavior you confirmed and the specific gap you will address. Then carry out the focused slice, keeping the developer updated if work takes time. Do not ask the developer to restate the project purpose or authorize ordinary implementation and verification within this assignment.
