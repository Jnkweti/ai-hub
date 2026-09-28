# Resident sessions and the shared event stream — 0.13.0 checklist

Scope: migration step 1 of the target design. Items are checked only with recorded evidence in
[LIVE-STREAM-0.13.0.md](LIVE-STREAM-0.13.0.md).

- [x] Pipe host lives for a phase; dispatches attach and detach; limits reset per dispatch; detached calls are refused, not counted.
- [x] Provider clients stay resident across dispatches of a phase and end with the phase.
- [x] Ordered event stream in the ledger with bounds, clipping, eviction, validation and additive defaults.
- [x] `get_events` tool registered for both providers, with instructions updated.
- [x] Delta prompts for later turns; full core for first turns and synthesis; exact deltas saved as manifests.
- [x] Regression cases added; existing suite passes.
- [x] Package built and desktop smoke checks passed.
- [x] Live provider check: both CLIs complete a second turn on the resident session with the bridge attached to a new dispatch (`artifacts\live-stream-013-native-routing-final`).
- [x] Documentation and version updated.
- [x] Installed with the user's approval on September 28, 2026; reopened idle.
