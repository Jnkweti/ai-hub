# Selectable collaboration strategy — 0.31.0

The plan's Phase 2 item "keep independent assessment … and other experimental strategies as selectable policies", and
the prerequisite for Phase 5's bounded action set. The first alternative to the default reaction rounds is the one
the [pilot baseline](PILOT-BASELINE-0.28.0.md) already exercised by hand: independent answers, then synthesis.

## Strategies

| Strategy | Flow for a message to both agents |
| --- | --- |
| Reaction rounds (default) | The first speaker answers; the peer, optionally prepared in parallel, contributes or passes with the first answer in hand; every contribution gives the other participant a reaction opportunity; explicit requests, questions and resolutions route as before. |
| Independent answers, then synthesis | Both agents answer from the phase-start common context without seeing each other (no preparation, no `PRECEDING AGENT RESPONSE`, an `INDEPENDENT ANSWER` note, and the repeat check off, since two independent answers may agree). Once both have answered, the first speaker gets a synthesis turn on its resident session carrying the peer's full answer and a `SYNTHESIS STEP` instruction: produce the single best answer, adjudicate differences by checking the files rather than averaging, keep what only one found if it holds, state what remains unverified. The phase ends after the synthesis unless it asks the peer something, in which case the usual routing (question → answer → resolution) continues. |

The strategy yields to the usual routing when the message names one agent, splits asks with @mentions, needs no peer
(a greeting or exact-answer request), or finds a participant unavailable. Every phase records its strategy in the
stream's phase-start event, the assignments carry the roles `independent contribution` and `synthesis`, and the
feedback store's strategy fingerprint includes the setting, so later outcomes can be compared per strategy.

## Where

Settings → Collaboration strategy (a combo box; the choice applies to new phases in every conversation).
`HubSettings.Strategy` ("reaction" | "independent"), `HubCoordinator.Strategy`, the `Synthesis` opportunity slot, and
the pilot runner's `AIHUB_PILOT_STRATEGY=independent`.

## Verification

- Build: zero warnings, zero errors. Full regression suite: **271 tests passed** — three new cases: the second
  answer is formed without the first and without preparation, the synthesis turn carries the peer's answer on a delta
  prompt and no reactions follow, the roles, stream events and state are recorded; the strategy yields to an
  addressed message, a greeting and split asks, and the default strategy is unchanged and recorded; a synthesis that
  asks the peer a question routes, is answered and comes back for a decision, with exactly one synthesis per phase.
- Package `artifacts\strategy-0310-release` (0.31.0.0) passed the three smoke checks; installed on October 3, 2026
  after closing the idle 0.30.0 app (`artifacts\strategy-0310-install-result.json`: 612 files verified, hashes
  match, data unchanged, backups `before-collaboration-20261003-182539.zip` and
  `before-collaboration-data-20261003-182539.zip`); reopened as 0.31.0.0 with 5 rooms and 2 tasks.
- Live (`artifacts\pilot-028\strategy7`, the second pilot task, both agents, `AIHUB_PILOT_STRATEGY=independent`): the
  phase-start event names the strategy; Codex answered first (3 min 47 s, ran the CLI, both defects, -55.00); Claude
  Code answered from an 11,223-byte full core that held nothing of Codex's answer (1 min 53 s, both defects, the
  -75/-60/-55 decomposition and the tests' blind spots; the assignment is recorded as `independent contribution`); the
  host then queued the synthesis for Codex, whose 9,469-byte delta prompt carried Claude's full answer; the synthesis
  (1 min 40 s) kept the shared diagnosis, verified Claude's test-gap claim against the test file before adopting it,
  and stated what remained unverified; the phase ended there. 7 min 20 s in total — the same as the reaction-rounds arm
  on this task — with Codex 1.02M input tokens (956k cached) / 8.5k output and Claude $0.77. The result is equivalent
  in content to the reaction-rounds arm; the difference is procedural: each agent's view is formed before it sees the
  other's, and the synthesis is a single accountable step. One run.
