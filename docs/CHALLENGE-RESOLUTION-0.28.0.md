# Question resolution — 0.28.0

First harness slice from [CLAUDE-HANDOFF.md](../CLAUDE-HANDOFF.md) (October 3, 2026) toward [PROJECT-VISION.md](../PROJECT-VISION.md):
make one task-relevant reciprocal exchange observable and traceable, on the existing coordinator and provider clients.

## What already existed (confirmed from source and tests, not from the plan documents)

- A phase runs one resident native session per agent with an opportunity queue (`HubCoordinator.RunAsync`): the first
  speaker contributes; the peer gets an "initial contribution" slot, optionally with tool-free preparation notes; after
  every non-quiet contribution each other participant gets a reaction slot framed as "speak only if new events create a
  specific addition, otherwise pass silently". Passes are `agent_pass` events; the phase ends when everyone passes, and
  `PhaseSummary` names unanswered peer requests and open, addressed or disputed findings.
- Explicit peer requests (`handoff`, `review_request`, `question` with `recipient`) jump the queue and reach the recipient
  as `CURRENT STRUCTURED PEER MESSAGE`; the terminal answer must carry `reply_to`, and the request is marked `Answered`.
  Review results are forced back to the author; `disputed` findings exist; input manifests record exactly what each
  provider received; the user can interject, pin instructions and stop; permissions stay with the CLIs.

## The gap

The challenge loop was open on the asker's side. When agent A asked agent B a `question` (the contract's only
challenge type) and B answered with a `status` + `reply_to`, the host marked the question answered and gave A only a
generic reaction slot. A was not told its question had been answered, did not receive the structured answer, was
steered toward a quiet pass, and the ledger could not distinguish "A accepted the answer" from "A had nothing to add",
let alone record whether A revised or retained its position. The peer's initial-turn prompt invited additions, never a
focused challenge, and no fixture exercised `question` end to end.

## Changes

| Change | Effect | Where |
| --- | --- | --- |
| Resolution turn | When a terminal message answers a peer's `question` (the answer carries `reply_to` and no `recipient`), the host withdraws the asker's plain reaction slot and puts a resolution slot at the front of the queue. Its assignment is `RESOLUTION OF YOUR QUESTION …`: the answer's summary and ID, the full prose as `PRECEDING AGENT RESPONSE`, the structured answer as `ANSWER TO YOUR QUESTION`, and the instruction to say what changed, or what still disagrees and what evidence would settle it, or to pass with `no_further_contribution` to accept the answer. The follow-up "pass silently" framing is not used on this turn. | `HubCoordinator` (`Opportunity`, `ResolutionAssignment`, `Speak`) |
| Ledger links | The dispatch carries `AnswerMessageId`; `OpenDispatch` accepts it only for a peer's successful answer, in this run, to a `question` this agent sent and that is `Answered`. The asker's terminal may set `reply_to` to the answer (other IDs are still rejected). `get_task_context` returns `answered_question` with both messages. The assignment is recorded with role `resolution`, goal = the question's `requested_action`, dependencies `[question id, answer id]`. The stream gets a system event (`ref` = answer id) saying who answered what and that the asker decides next; an accepting pass is recorded as `agent_pass` "Accepted the answer to its question; nothing further." instead of "Reviewed; nothing to add." | `CollaborationStore.OpenDispatch`, `ValidateReferences`, `AnsweredQuestion`, `CollaborationDispatch.AnswerMessageId` |
| Invitation to challenge | The peer's initial contribution prompt now says: if you disagree with a specific claim in the preceding response, or it rests on an assumption you can name, submit a `question` naming the claim and what evidence would settle it, instead of a second standalone answer; the Hub returns the answer for a recorded decision. The dispatch instructions describe the resolution turn in two sentences. | `HubCoordinator.Speak` ("Both" block), `CollaborationDispatch.Instructions` |
| Fallbacks kept | Automatic collaboration off: the answer pauses the run ("… explicitly continue to let X respond"), as explicit peer requests already do. An unavailable asker gets no resolution slot. Resolution turns count toward the round cap and the repair budget. `handoff` and `review_request` answers are unchanged (review has its own return path). Only `question` triggers a resolution. No schema or stored-format change: assignments, events and `reply_to` already existed; old ledgers load unchanged. | `HubCoordinator` turn loop |
| Codex default-model fallback | Found while running the live check: the user's ChatGPT-account Codex now rejects `gpt-6.1-sol` at turn time, and the 0.24.0 fallback reconnected with `thread/resume`, which on Codex CLI 0.159.1 keeps the thread's original model, so the retry failed identically and the phase paused. The retry now starts a fresh thread without a model (the failed thread held nothing but that turn). The test fake resumes threads the same way the CLI does. Without this, Codex in the installed app fails every phase until a model is set in Settings. | `CodexClient.SendAsync`, `tests/AIHub.Tests/Program.cs` fake Codex |

## Verification

Fixture results and live results are listed separately.

- Build: zero warnings, zero errors.
- Full regression suite (fixtures): **255 tests passed** — the 249 of 0.27.0, the two activity-snapshot cases already in
  the uncommitted tree, and four new cases in `tests/AIHub.Tests/ChallengeResolutionTests.cs`: an answered question
  returns to the asker for a recorded decision (revised: 5 turns, the answerer gets one reaction afterwards; accepted: 4
  turns with voluntary follow-ups off, an accepting pass is recorded distinctly and hidden from chat; the resolution prompt
  carries the answer and not the follow-up framing; the assignment links both IDs; `reply_to` to the question is
  rejected); automatic collaboration off pauses after the answer; a resolution dispatch is refused for an unknown answer,
  for the question itself, and for the answerer resolving its own answer. The existing fallback case passes against the
  fake that now keeps a resumed thread's model.
- Live check with the real CLIs (`AIHub.Tests.exe --challenge-live artifacts\challenge-live-028`, Codex CLI 0.159.1 and
  Claude Code, October 3, 2026, prescribed moves): Codex claimed, Claude Code asked the prescribed question, Codex answered
  with `reply_to`, and Claude Code's resolution turn ran on its resident session with a 4,993-byte delta prompt containing
  `RESOLUTION OF YOUR QUESTION` and `ANSWER TO YOUR QUESTION`. Ledger: `status`, `question` (Answered), `status` replying
  to the question, `status` replying to the answer; assignments `contribution`, `contribution check`, `peer assignment`,
  `resolution` (dependencies = question id, answer id; completed); stream `user_message, system, agent_message,
  agent_message, agent_message, system (answered …; decides next), agent_message, system (run ended)`. Claude's recorded
  decision: "Codex's answer does not change my view, because surviving restarts is exactly the property that makes local
  …" — a retained position, visible in chat and linked by `reply_to`. Two provider processes for four turns; host inputs
  Codex 12,011 then 4,512 bytes, Claude 16,104 then 4,993 bytes. Usage: Codex 229k input (187k cached) / 2.5k output
  over 2 turns; Claude 185k input (151k cached) / 1.0k output, $0.78. One expected `gpt-6.1-sol` rejection preceded the
  fallback. Evidence: `artifacts\challenge-live-028\results.json` and `events.json` (git-ignored).
- Not done: desktop smoke checks, package build and installation (the handoff excludes installation from this slice).

## What this does and does not show

The mechanism works: a question, its answer, and the asker's decision are now one traceable chain in the ledger, the
stream and the chat, and the user can stop or redirect at every turn. The live run prescribed every move, so it shows the
host's routing and records, not that the agents challenge each other unprompted or that the exchange improved an answer.
One run is not a measurement. The decision itself is prose plus `reply_to`; there is no typed revise/retain field, which
keeps the published submission schema unchanged. `handoff` answers still arrive as generic follow-ups.

## Next milestone

Phase 0/2 of [EMERGENCE-AND-LEARNING-PLAN.md](EMERGENCE-AND-LEARNING-PLAN.md): run a real, unprescribed task through
the loop and record whether the invitation produces useful questions, how often the asker revises, and the cost; capture
a solo-provider starting point for the same task. If prose decisions prove ambiguous, add a typed decision to the
contract with a schema version. Consider routing `handoff` answers the same way.
