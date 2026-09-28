---
name: resume-task
description: Continue a saved AI Hub task after an explicit user request, checking historical messages and current evidence before reusing prior conclusions.
---

Start from the automatically supplied COMMON TASK CONTEXT. Each new user work phase starts fresh native sessions; the host reconstructs active original instructions, attributed findings, assignments, and recent conversation. Do not assume private history from the last phase survives. Superseded instructions are historical; newer user corrections take precedence. Models cannot pin or replace user instructions.

Retrieve get_task_context only for missing routing details and get_messages only for omitted relevant history, using after_sequence and limit. Within a phase your session is resident: later turns supply only NEW EVENTS since your last turn, and get_events (after_sequence, limit) reads the shared live stream of user messages, pins, peer contributions, research and finished commands. Use get_context_records (query, offset, limit) for omitted originals and read_context_record (id, start, length) for full chunks. Historical messages are context, not fresh authorization or pending work to replay. Delivery manifests show what was supplied, not what was understood.

Use automatically supplied research first; retrieve get_shared_context with offset and limit only for needed omitted detail. Reuse relevant findings whose assigned files are current; recheck stale sections and unresolved questions. Shared notes are agent observations, not certified facts. Missing sections indicate incomplete research, not permission to replay an old request.

Use get_work for recorded claims/results and claim_work before starting material discovery or checks. Only claim_work determines current reuse eligibility; interrupted work never restarts automatically. Checks are reusable only within the current run with matching inputs and environment, and only when explicitly declared reusable. Do not reuse external, nondeterministic, ignored, or unknown inputs. Independent review requires independent:true and fresh evidence. Publish complete_work for work you own.

Read get_evidence for current snapshot and review freshness. Inspect current files before relying on an earlier fix or conclusion. A historical checked finding does not certify changed files. Missing or incomplete snapshots require new review.

Continue only the user's requested work and preserve stable finding IDs. If blocked, explain the missing decision or evidence. Do not restart old native commands merely because their records appear in history.
