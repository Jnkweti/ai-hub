# Default Codex model — 0.24.0 checklist

- [x] Blank Codex model means gpt-6.1-sol; an explicit model still wins.
- [x] Fallback to the CLI default on rejection at thread start or at turn time, remembered for the session, with a status notice.
- [x] Sending directly to a provider marked unavailable clears the mark.
- [x] Settings label says what blank means.
- [x] Regression cases added; existing suite passes.
- [x] Package built and desktop smoke checks passed.
- [x] Live checks with real providers: routing round trips (both orderings) and the live-stream reaction round passed; the ChatGPT-account rejection of gpt-6.1-sol was observed and the fallback handled it.
- [ ] Clean checkout built and passed the full suite.
- [ ] Installed with the user's approval.
