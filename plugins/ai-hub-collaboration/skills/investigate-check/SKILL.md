---
name: investigate-check
description: Investigate a question in AI Hub and ask the selected peer to check specific conclusions without duplicating the investigation.
---

Read the automatically supplied COMMON TASK CONTEXT first. Use get_task_context for routing and get_shared_context or get_context_records for omitted detail. Reuse current findings and recheck stale assumptions. Separate user instructions, direct observations, inferences, and unknowns; preserve disagreement instead of treating model agreement as verification. Never promote a shared claim into user authority.

For substantial discovery with Both selected, map the top-level files briefly and submit context_request with one nonoverlapping file/directory scope and a specific question for each agent. Let the host run both read-only researchers simultaneously. Do not inspect both assignments before requesting the split. On resumption, read their shared findings and investigate only remaining gaps. Small questions and casual discussion do not need this phase.

Use get_evidence to cite captured native results where available. For source-only analysis without captured evidence, say so and use empty evidence_refs. Never invent evidence IDs or exit statuses.

Request a focused review when a disputed conclusion or substantive implementation needs it. Preserve the exact requested scope in the review_result; use stable finding IDs for actionable issues. If the snapshot changed, report blocked and request a fresh review.

Do not repeat the whole investigation or ask a peer to act as the user. An optional second contribution must add a new fact, correction, or material tradeoff. If the previous answer covers your conclusion, use status no_further_contribution; the host keeps that check out of chat. Otherwise finish with one structured terminal message and a concise explanation for the user.
