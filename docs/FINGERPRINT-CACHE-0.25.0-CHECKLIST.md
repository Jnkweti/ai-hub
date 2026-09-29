# Fingerprint cache and snapshot skipping — 0.25.0 checklist

- [x] Per-workspace hash map persisted and reloaded; corrupt files ignored; results identical.
- [x] Status-only messages take no snapshot; reviews, findings and completions with findings still do.
- [x] Regression cases added; existing suite passes.
- [x] Package built and desktop smoke checks passed.
- [ ] Clean checkout built and passed the full suite.
- [ ] Installed with the user's approval.
