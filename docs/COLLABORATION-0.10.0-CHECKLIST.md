# Concurrent preparation and shared work — 0.10.0

Scope: both selected models receive the user message immediately; speaking stays ordered. The waiting model prepares from the supplied context, then incorporates the first response before contributing. Shared task-scoped work claims prevent cooperating agents from repeating discovery/checks. Native commands retain native permissions; independent review remains explicit.

- [x] Start preparation alongside the designated speaker using one frozen common snapshot. Keep the current speaker first when a running task is corrected.
- [x] Preparation has no execution/editing tools, no chat publication, and no authority to complete the task. Persist its input and assignment lifecycle.
- [x] Reconcile preparation with the completed first response and latest user inputs before the waiting agent speaks. Preserve native session continuity when supported.
- [x] Cancel and join preparation on Stop, correction, failure, user-blocked work, research split, and simple-answer completion. No replay after restart.
- [x] Add bounded durable work claims, in-progress lookup, result publication, abandonment/retry, evidence references, and host-bound owner/generation checks.
- [x] Reuse discovery only for matching scoped inputs; reuse checks conservatively within a live generation and matching whole-workspace/environment identity. Explicit independent checks bypass reuse.
- [x] Preserve native execution provenance and existing independent-review requirements. Never treat an agent summary as successful command evidence.
- [x] Supply existing findings directly; retrieve omitted detail only. Count both research contributions before synthesis so a general peer turn is not automatically repeated.
- [x] Deduplicate identical scoped freshness scans within a context/read operation; retain fresh captures at execution/review boundaries.
- [x] Expose preparation/work ownership/reuse in activity, shared context, and export. Update bundled skills and documentation.
- [x] Verify concurrency, ordering, continuation, invalidation, ownership, stale/restarted state, bounded results, actual evidence reuse, and independent checks.
- [x] Run regression, real-provider, workflow/schema, and desktop checks; package, back up, install, and reopen idle.

Reviewed constraints: native tool calls that bypass the shared-work protocol cannot be transparently intercepted by the current adapters. Claims govern cooperative execution through the new tools; bypasses must not be represented as deduplicated work. Cache keys cannot establish the stability of external services, nondeterministic tests, or untracked environment changes; those checks must be marked nonreusable. Shared state is not a shared inference cache. Preparation adds a bounded provider call and may not reduce total latency/cost for small tasks.
