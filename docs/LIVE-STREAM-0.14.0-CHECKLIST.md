# Reaction rounds and user interjections — 0.14.0 checklist

Scope: migration steps 2 and 3 of the target design. Items are checked only with recorded evidence in
[LIVE-STREAM-0.14.0.md](LIVE-STREAM-0.14.0.md).

- [x] Opportunity queue replaces host speaker selection; explicit requests first; research synthesis then peer reaction.
- [x] Phase ends when every participant passes; simple-request, round-cap, blocked and waiting exits retained.
- [x] Auto collaborate off and follow-ups off give one opportunity per participant.
- [x] Repeat guard demoted to diagnostics; `StreamImbalance` diagnostic added.
- [x] User interjection joins a running phase as an event with user authority; desktop routes running-room messages to it.
- [x] Regression cases added; existing suite passes.
- [x] Package built and desktop smoke checks passed.
- [x] Live provider check: review round trip on resident sessions, both directions.
- [ ] Live provider check: reaction rounds with a mid-phase user interjection. Partial: interjection, stream entry and Claude's reaction verified live; Codex's reaction hit the account usage limit (reset October 3, 2026). Rerun `AIHub.Tests.exe --live-stream-live <fresh dir>` after the reset.
- [x] Installed with the user's approval on September 28, 2026, accepting the partial live check; reopened idle.
