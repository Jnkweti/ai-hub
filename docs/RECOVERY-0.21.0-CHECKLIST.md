# Bounded recovery loops — 0.21.0 checklist

- [x] Process exit mid-turn distinguished from other provider failures.
- [x] Backoff restarts with the same native session; user told each time; audit code recorded.
- [x] Circuit breaker after the last interval; Stop still interrupts.
- [x] Regression cases added; existing suite passes.
- [x] Package built and desktop smoke checks passed.
- [ ] Clean checkout built and passed the full suite.
- [ ] Installed with the user's approval.
