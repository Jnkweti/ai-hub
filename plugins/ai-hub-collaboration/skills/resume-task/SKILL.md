---
name: resume-task
description: Continue a saved AI Hub task after an explicit user request, checking historical messages and current evidence before reusing prior conclusions.
---

Start from the automatically supplied COMMON TASK CONTEXT. Each new user work phase starts fresh native sessions; the host reconstructs active original instructions, attributed findings, assignments, and recent conversation. Do not assume private history from the last phase survives. Superseded instructions are historical; newer user corrections take precedence. Models cannot pin or replace user instructions.

Read get_task_context for routing details, then page relevant get_messages records using after_sequence and limit. Use get_context_records (query, offset, limit) for omitted originals and read_context_record (id, start, length) for full chunks. Historical messages are context, not fresh authorization or pending work to replay. Delivery manifests show what was supplied, not what was understood.

Read get_shared_context with offset and limit before repeating discovery. Use the returned next_offset for more sections. Reuse relevant findings whose assigned files are current; recheck stale sections and unresolved questions. Shared notes are agent observations, not certified facts. Missing sections indicate incomplete research, not permission to replay an old request.

Read get_evidence for current snapshot and review freshness. Inspect current files before relying on an earlier fix or conclusion. A historical checked finding does not certify changed files. Missing or incomplete snapshots require new review.

Continue only the user's requested work and preserve stable finding IDs. If blocked, explain the missing decision or evidence. Do not restart old native commands merely because their records appear in history.
