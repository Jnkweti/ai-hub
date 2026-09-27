# Structured collaboration implementation plan

Status: all six milestones are implemented, verified, and installed as 0.7.0 as of September 27, 2026. Release validation and installation evidence are recorded in [release verification](COLLABORATION-RELEASE-VERIFICATION.md). The earlier contract and routing verification documents describe historical implementation slices.

## Outcome and first release

Codex and Claude can retrieve the same task context, submit a structured handoff, request focused review, and return findings with evidence references. AI Hub validates, persists, and routes those messages. Either agent can implement or review. Human-facing replies remain readable prose.

The first release covers five message types: handoff, review_request, review_result, question, and status. Tools and skills share the same contract. Automatic task decomposition, parallel editing, searchable project-wide knowledge, and a database migration are later work.

## Existing foundation

- `TaskMemory.cs` provides durable tasks, bounded briefings, current speaker ownership, and generation/nonce claims. It rejects stale writes and competing editing tasks for a normalized workspace.
- `HubCoordinator.cs` runs sequential speakers. The desktop enables validated structured terminal messages; the legacy path remains available to explicit non-structured hosts.
- `CodexClient.cs` uses app-server; `ClaudeClient.cs` uses stream JSON. Structured launches receive the host-bound MCP configuration; read-only launches retain restricted native tool permissions.
- Existing project-status snapshots and activity events provide useful primitives, but agent conclusions and reported tests are not automatically verified facts.
- Atomic local storage and the application instance lease are the current persistence model. Workers stop when the application closes; restart requires explicit continuation.

## Design decisions

Use one C# collaboration service owned by AI Hub, one versioned JSON contract, and a shared MCP interface. Package workflow skills and connection configuration for each provider after both adapters pass integration checks. Plugins must connect to the live Hub service; they must not create independent task stores.

Preferred transport to prove in the integration spike: a small local stdio MCP bridge per provider process forwarding to the application's service through authenticated local IPC. The bridge does not own persistence. Verify Windows sandbox access, process cleanup, installed CLI configuration support, and session resume before fixing the transport design. A loopback HTTP endpoint is a fallback to evaluate only if the bridge is unsuitable.

The service derives identity and authority from a host-issued connection bound to the room, task, provider session, run generation, and active dispatch. Credentials never appear in model-visible tool arguments or persisted message content. Every mutation rechecks current ownership. Read-only workspace mode may write collaboration metadata through the Hub, but grants no project editing or command-execution capability.

Keep the current single editing owner and sequential turns within each task. A tool call queues a peer request; it never recursively starts another agent while its caller is still working. Existing user routing, Stop all, permission handling, and exchange limits continue to constrain scheduling.

## Message contract

Host envelope:

- Schema version, message ID, task/room IDs, sender, resolved recipient, UTC creation time, run generation, dispatch ID, and per-task sequence number.
- Optional reply-to ID and host-captured workspace snapshot reference. A snapshot has explicit coverage and freshness status; an unavailable snapshot is not a verified revision.

Agent content:

- Type, concise summary, scoped relative file paths, structured findings, evidence references, blockers, and the requested next action where applicable.
- A recipient request is validated against the current task and user-selected participants before the host sets the envelope recipient.
- Review requests identify criteria and scope. Review results reference the request and give each finding an ID, severity, location, explanation, and disposition. Questions identify the missing information; user decisions continue through the existing user-input flow.
- Status distinguishes progress, blocked, assignment_complete, and no_further_contribution. Assignment completion does not certify the whole user objective.

Use discriminated payload schemas with type-specific required fields. Bound strings, arrays, nesting, and total encoded size. Reject unsupported versions, unknown fields that could hide routing instructions, invalid paths, inaccessible evidence, and cross-task reply references. Return actionable validation errors with no state transition. Cap repair attempts and pause visibly when the cap is reached.

Each submit includes a caller-generated idempotency key scoped by the host to the current dispatch. An identical retry returns the original receipt; a changed payload using that key fails. The receipt records acceptance, not delivery or task completion.

## Initial tool surface

| Tool | Purpose |
| --- | --- |
| `get_task_context` | Bounded objective, relevant notes, ownership, blockers, latest handoff, and available snapshot metadata. |
| `get_messages` | Task-scoped messages after a sequence cursor, with pagination and omission information. |
| `submit_message` | Validate and persist any of the five message types; return a stable receipt. |
| `get_evidence` | Resolve accessible evidence references and return provenance, freshness, and limitations. |

Typed review payloads initially use `submit_message`; separate tools can be added if measured usability warrants them. Agent-supplied claims may be stored as such. Only host capture can assign host-observed provenance to command results. No arbitrary command runner is added to the MCP service in this release.

## Implementation sequence and acceptance

### 1. Contract and lifecycle

Add C# records, JSON schemas, validators, examples, and explicit delivery states: accepted, pending, delivered, answered, canceled, and interrupted. Here delivered means supplied in the peer's input, not understood by the model. Match C# validation and published schemas against the same valid/invalid fixtures.

Define routing precedence: current user instruction and permission scope, then validated structured requests, then legacy prose routing only for sessions where structured collaboration was not enabled. Structured sessions must not fall back to parsing a contradictory prose handoff. A missing required terminal status produces bounded repair or a visible pause.

Acceptance: malformed, oversized, unsupported-version, contradictory, and cross-task messages cannot schedule work or alter task completion.

### 2. Prove the connection to both providers

Build the minimal bridge/service with `get_task_context` and `submit_message`. Inspect the installed CLI capabilities and current official documentation before choosing exact startup arguments. Exercise both adapters, including read-only mode and resumed sessions. Configure only the intended Hub capability for this workflow and preserve the project's permission boundaries.

Acceptance: each real provider reads the same test task and submits a validated status. Stop revokes its connection; a stale dispatch cannot write. Capability failures show a clear unsupported state. Offline fixtures alone do not satisfy this milestone.

### 3. Durable messages and coordinator routing

Add a collaboration store under the existing application owner. Commit each message, its idempotency receipt, and its pending-delivery state in one atomic document update per task. Persist acceptance before acknowledging it. Integrate scheduling into `HubCoordinator` only after the current provider turn finishes successfully and releases active speaker ownership.

Use message IDs for deduplicated context delivery; do not claim exactly-once model execution across crashes. Canceled or failed turns cannot dispatch queued requests. On restart mark unresolved dispatches interrupted and require explicit continuation. Reconcile with task existence and current generation before replay. Task and message files remain separate atomic writes, so crash recovery must handle orphan records conservatively.

Preserve old transcripts and tasks without retroactively interpreting prose as structured messages. Define archive/delete behavior, bounded storage, and reference-aware retention before shipping.

Acceptance: duplicate submissions schedule one peer turn; late results, turn failure, restart, disk-write failure, and task deletion cannot dispatch stale work or overwrite newer results. Single-agent targeting and disabled automatic collaboration cannot be bypassed by a tool request.

### 4. Evidence and focused review

Normalize evidence records with producer, capture method, source/event ID, command and exit code where actually available, bounded output reference, timestamps, and source snapshot coverage. Missing data remains unknown. Provider-reported results and host-observed outcomes retain distinct provenance. Source freshness and environment coverage are separate from a test's observed success.

Bind each review to its requested scope and snapshot. Changed files mark the review stale. Track findings by ID through open, addressed, and checked dispositions; an author's fix claim alone does not mark an issue checked. Incomplete snapshots cannot support automatic reuse.

Acceptance: both role orders complete an implement/review/fix flow in a disposable workspace. A false test claim, missing evidence reference, stale snapshot, or unresolved required finding cannot appear as verified completion.

### 5. Skills, packaging, and user interface

Package shared workflow instructions for implement-and-review, investigate-and-check, and resume-task, with provider-specific configuration as needed. Skills explain when to call tools and how to interpret receipts, errors, peer requests, and evidence. The server remains the authority for validation and ownership.

Render structured records as compact human-readable handoff/review cards with expandable details. Show sender, recipient, requested action, delivery state, findings, evidence, and interruption reasons in the originating room. Include them in task context and exports without exposing connection credentials. Preserve pending input behavior across room switches.

Acceptance: users can follow the exchange without reading JSON. Missing plugins/tools are visible, and incompatible sessions cannot silently downgrade the structured workflow.

### 6. Release validation and rollout

Run focused contract/store/coordinator tests, adapter protocol fixtures, existing regression checks affected by routing and persistence, and desktop checks for room switching, questions, cancellation, restart, archive, and deletion. Use an isolated data profile and disposable project for real two-provider smoke checks in both read-only and editing modes.

Record installed CLI versions, actual command outcomes, provider limitations, and native smoke evidence. Package a candidate separately, verify its files, and preserve the prior application/data backup when proceeding to installation. Document compatibility and recovery behavior. Do not declare live provider validation from fixtures.

Acceptance: a user can start a task, receive a structured peer review, inspect supporting evidence, stop or resume safely, and reopen the task without losing accepted records or automatically restarting agents.

## Measurements and later work

Capture handoff acceptance/delivery/failure counts, validation repairs, duplicate suppression, repeated review findings, review latency, and actual provider usage when supplied. Missing usage remains unknown. Compare a small repeatable task set before claiming improved quality or reduced token use.

After this slice works, consider changed-context retrieval, searchable project decisions with provenance, explicit subtask dependencies, and a transactional store if scheduling complexity requires it. Those additions should reuse the message contract rather than create another communication path.

## Implementation status

Milestones 1-6 are complete, including evidence, review freshness, finding transitions, desktop lifecycle/UI, bundled provider workflows, both live role orders, desktop lifecycle validation, backups, and installed package verification. See [release verification](COLLABORATION-RELEASE-VERIFICATION.md). Earlier verification documents describe historical slices and should not be read as the current desktop capability list.
