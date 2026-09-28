---
name: adversarial-review
description: Review a peer's work to break it, not to confirm it, when the review_request's scope.focus includes "adversarial" or the user asks for an adversarial review.
---

Apply this workflow when the incoming review_request's scope.focus contains "adversarial", or the user asked for an adversarial, red-team or challenge review. Otherwise use implement-review.

Your job is to find what is wrong, missing or fragile. Assume the author's summary is optimistic. Do not restate what works; a clean adversarial review says so in one line and reports an empty findings array.

Start from the requested files and the author's cited evidence. For each claim in the request, ask what would have to be true for it to hold, then check that directly: read the code path, run the existing checks through native tools, and where authorized add a targeted probe (an edge input, a failing precondition, a concurrent call, an empty or oversized value). Cite host-captured evidence IDs from get_evidence for anything you ran; unknown exit codes stay unknown.

Look specifically for: boundary and off-by-one cases; error paths that swallow or misreport failures; state that is read and written without the same guard; inputs that cross a trust boundary without validation; assumptions about ordering, timing or the file system; tests that pass without exercising the change; and behaviour the request did not mention but the change affects.

Report each problem as a finding with a stable ID, severity, relative path, line, a concrete failure scenario (inputs and state that lead to the wrong result), and disposition open. Rank by severity. If you believe an existing finding was wrongly marked addressed, return it as disputed and say why. Keep the review scope exactly equal to the request's scope.

Do not fix anything, do not broaden the task, and do not soften a finding because the author is a peer. If coverage was incomplete or files changed while you worked, report blocked so a fresh review can be requested.
