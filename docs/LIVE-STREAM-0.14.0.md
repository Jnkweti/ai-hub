# Reaction rounds and user interjections — 0.14.0

Migration steps 2 and 3 of the target design. Step 1 (0.13.0) made provider sessions resident and gave
every participant one shared event stream. This release changes who decides that an agent speaks, and
lets the user speak while agents work.

## Turn-taking by intent

The coordinator keeps an ordered queue of opportunities instead of choosing the next speaker
(`HubCoordinator.cs`, structured loop):

- The phase starts with one opportunity per participant. The addressed agent, then the agent interrupted
  by the last Stop, then rotation from the previous phase decide only who goes first.
- Every non-quiet contribution offers an opportunity to each other participant.
- An explicit peer request (handoff, review request, question) places the recipient's opportunity first
  with the request attached, as before.
- A research split places the requester's synthesis first and then offers the peer a reaction.
- A participant answers an opportunity with a contribution or a quiet pass. The phase ends when the queue
  is empty ("Every participant passed on the newest events"), when a greeting or exact short answer has
  been answered once, at the round cap of `2 + MaxAutoRounds × 2` turns, or on a blocked status or a wait
  for user input.
- Auto collaborate off, or voluntary follow-ups disabled, withdraws further opportunities from any
  participant that already contributed, so each gets exactly one of its own.

The repeat guard is kept and demoted: a near-repeat is still hidden from chat and recorded as a
`RepeatedContribution` diagnostic. A new `StreamImbalance` diagnostic records a phase in which one
participant contributed three or more times while the other never did, so crowding and silence can be
measured before the guard is retired.

## The user as a peer

`HubCoordinator.InterjectAsync` accepts a message while a phase runs. The desktop appends it to the room
and records an activity notice; the loop, after the current turn, synchronizes context so the message
becomes a `user_message` event and an active instruction with user authority, then offers every
participant a fresh opportunity. A participant's first turn carries the message in its common core; a
resident turn carries it as a stream event; a participant that already saw it is not sent it again. The
running provider turn is never cancelled. Stop all remains the explicit interrupt, and a message sent
while the room is idle starts a new phase with fresh sessions as before. Progress questions and project
status requests keep their existing paths.

## What did not change

Ownership, the contract, delivery states, evidence, snapshots, work claims, preparation, delivery cursors,
pins, cards and approvals are unchanged. An agent-side interrupt marker was not added: explicit peer
requests already give a participant the next opportunity.

## Verification

- Build: zero warnings, zero errors.
- Full regression suite: **205 tests passed**: the 203 of 0.13.0, one updated to expect the peer's reaction
  after a research synthesis, plus two new cases in `LiveStreamTests`: a user message during a running
  phase joins the stream without restarting it and is supplied exactly once per participant; reaction
  order across four turns ending when everyone passes, with no imbalance diagnostic.
- Package: `Build.ps1` to `artifacts\live-stream-014-release`, 611 files, version 0.14.0, identical Core hashes in
  the app and the bridge (`artifacts\live-stream-014-build.txt`).
- Desktop smoke checks against the package: `tests\Local-Diagnostics-Smoke.ps1` and
  `tests\Conversation-Management-Smoke.ps1` passed (`artifacts\live-stream-014-local-diagnostics-ui.txt`,
  `artifacts\live-stream-014-conversation-ui.txt`).
- Live providers, routing (`artifacts\live-stream-014-native-routing`, 129 seconds): both directions of the review
  round trip passed on resident sessions with the packaged bridge, followed by a fresh-phase continuation and a
  restart check.
- Live providers, reaction rounds with a mid-phase interjection (`artifacts\live-stream-014-native-stream`,
  log `live-stream-014-native-stream-log.txt`): **partial**. Codex contributed from the full core; the user's
  constraint was accepted while Codex's turn was ending and entered the stream as a `user_message` with user
  authority; Claude's first turn carried it in its core and its contribution explicitly addressed both Codex's
  point and the constraint ("Codex left the crash-safety part as … so I'll fill that gap"); Codex's reaction was
  prepared as a delta prompt carrying the constraint and then failed because the Codex account reached its usage
  limit (reset October 3, 2026). The host recorded the assignment and input as failed and paused the room with
  the provider's message, which is the intended failure behaviour. The check's final assertions were not reached;
  it should be rerun once the quota resets.
- Clean checkout of commit `27f9bca` cloned to a short temp path: Release build with zero warnings and zero
  errors; **205 tests passed** in 66 seconds (`artifacts\live-stream-014-clean-build.txt`,
  `artifacts\live-stream-014-clean-tests.txt`).
- Final package rebuilt from the committed tree: `artifacts\live-stream-014-release`, product version
  `0.14.0+27f9bca…`, 611 files, Core DLL SHA-256 `3BCB05A0499A3B3B913285AAA6E3F3656290E5330152F9E4FA89773506530E4D`;
  the diagnostics smoke check passed against it.
- Installed **0.14.0.0** on September 28, 2026 with the user's approval, accepting the partial reaction-round live
  check. The host closed the idle app itself after confirming zero running or owned tasks and zero child
  processes. All **611** package files matched the candidate and all **nine** production profile files were
  unchanged. Backups: `artifacts\before-collaboration-20260928-150440.zip` and
  `artifacts\before-collaboration-data-20260928-150440.zip`. Reopened the normal profile as 0.14.0.0 with zero
  running tasks and zero child processes. Evidence: `artifacts\live-stream-014-install-result.json` and
  `artifacts\live-stream-014-reopen-result.json`.
