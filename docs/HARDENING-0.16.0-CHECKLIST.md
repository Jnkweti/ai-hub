# Hardening — 0.16.0 checklist

- [x] Oversized files fingerprinted by size and time; snapshots stay complete.
- [x] Run state fenced on the run epoch; Stop clears it immediately.
- [x] Conditional periodic save; Tasks window refresh only on change; progress answer off the UI thread.
- [x] Native session re-key reported, not fatal.
- [x] Adapter per-turn state and context cursors synchronised.
- [x] Per-task lazy ledger recovery, background startup pass, bounded cache.
- [x] Curated check environment for shared-work reuse.
- [x] Research split framing verified as resolved by 0.14.0.
- [x] Room reopen replays status without logging.
- [x] Relaxed wire escaping and 1 MiB bridge frame.
- [x] Preparation cancelled only by real tool items.
- [x] Small items: unused guard method, status store, streaming maps, subfolder imports, Codex decline.
- [x] Context records archived at the cap behind a watermark.
- [x] Regression cases added; existing suite passes.
- [x] Package built and desktop smoke checks passed.
- [ ] Clean checkout built and passed the full suite.
- [ ] Installed with the user's approval.
