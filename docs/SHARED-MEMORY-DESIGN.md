# Shared project knowledge and independent workers

Status: project-status reuse and the first durable task/background-worker milestone are implemented. Automatic decomposition, dependency scheduling, general project facts/retrieval and SQLite persistence remain proposals. This document separates implemented behavior from recommended later work.

## Implemented first slice

Check status assigns one inspector and, for Both agents, one focused reviewer. Settings exposes the preferred inspector; single recipients stay targeted. Fresh read-only native sessions isolate this task from each room's ordinary history. The host validates bounded structured reports, records original usage events, and saves one latest report per normalized workspace path. An OS-held exclusive file handle prevents duplicate status owners across app instances sharing the data directory; waiting requests recheck the cache before dispatch.

Reuse requires complete content fingerprints and manifest/Git state, matching status configuration, and age under 30 minutes. New/deleted/edited files invalidate the report, including edits to files already dirty in Git. Pre/post-run changes, unreadable/linked paths and documented scan bounds prevent reuse. Refresh and Forget are explicit UI actions. Archive retains the report; deleting its source conversation clears it. Existing copies in other conversations remain. These source checks do not certify external state or agent conclusions.

Validation of the status slice includes offline ownership/freshness/cancellation/retention checks, native UI fixtures, and a real two-file Codex inspection + Claude review followed by a warm request with no new provider turns. The later task milestone uses offline provider fixtures; its acceptance evidence is recorded separately.

## Implemented durable task milestone (0.6.0)

General conversation runs now claim a durable task, record their speaker and save bounded recent task notes and agent replies. Application-owned room workers continue across view changes, with results and live questions retained in their originating conversations. The Tasks and notes inspector shows workspace tasks and lets the user add notes, stop a task, open its conversation or explicitly select a saved task. Stop all and shutdown stop every room worker. Restart marks unfinished work Interrupted and never resumes automatically.

One editing claim per normalized workspace is enforced; independent reads can overlap. A generation and nonce reject stale results, and cancellation does not release a claim before its actual run finishes. An explicit new task creates a new room and native sessions; selecting an older task scopes transcript context by task ID and resets native sessions. Saved context is bounded and marked historical/agent-reported, without promoting it into verified project facts.

For this first multi-record milestone, persistence uses the existing atomic JSON store under the application's exclusive instance lease. SQLite remains the proposed migration when dependency/claim scheduling needs transactional multi-record updates. See [ARCHITECTURE.md](ARCHITECTURE.md) for implemented limits and [TASK-MEMORY-VERIFICATION.md](TASK-MEMORY-VERIFICATION.md) for validation.

## Goals

- The user can address both agents and see their messages, activity, assignments, and results.
- Agents working together share the relevant task context.
- Agents working on different tasks keep separate task context while reusing common project facts.
- Assign each piece of work once, reuse current evidence, and measure token usage.
- Workers run independently and report results without blocking the user's conversation with the hub.

## Original root cause and remaining general-chat behavior

The original general-chat path broadcast broad requests to both agents. The 0.5 audit replaced this with ordered turn-taking in discussion and editing modes. The 0.6 milestone adds durable ownership for that shared task. The peer receives the preceding reply before contributing; the host still does not decompose arbitrary requests into independently scoped sub-tasks.

General chat shares bounded unseen conversation entries (up to 200 entries and approximately 40,000 characters), plus the selected task's compact saved briefing. Providers retain their own native histories, with IDs stored on Room. Task IDs scope shared messages; explicit task selection resets native sessions. Project status has its separate report store and freshness validation. Task notes do not have project-status freshness certification.

## Recommended structure

The hub owns scheduling, persistence, permission scope, and the activity feed. Codex and Claude are workers that receive explicit assignments and bounded context. The coordinator can start concurrent independent reads, collect progress events, and accept a completion report without keeping the UI blocked.

The coordinator should be an application state machine. Model planning is used when a request needs decomposition; routine ownership, freshness checks, status transitions, and routing should use normal code rather than another model call for every event.

The application now owns room workers independently of the selected view. Changing conversations preserves work, output and live input callbacks. Each running task keeps its assigned workspace and permissions. Stop all and shutdown cancel all workers; archive/deletion cancel that room's worker; restart does not automatically resume work. A future scheduler can further separate multiple dependency-driven work items from their display conversations.

### Memory scopes

| Scope | Contents | Who receives it |
| --- | --- | --- |
| Project | Architecture map, conventions, decisions, known issues, reusable findings and evidence | Relevant records for workers in this workspace |
| Task | Objective, current user constraints, plan, assigned work, progress, intermediate findings, open questions | Agents assigned to this task |
| Agent session | That worker's native conversation and execution history | Its own provider session |

Unrelated task instructions and intermediate notes do not automatically become project facts. A completed task can publish reusable findings with provenance. Project memory is selected into a compact briefing; it is not appended wholesale to every prompt.

Bind provider sessions to workspace, task, agent, and relevant model/permission configuration. Keeping separate task files while reusing the same long native session would still carry earlier task context into the next task. Conversation IDs can link to tasks for display and history; a conversation is not the unit of task ownership.

### Task ownership

Represent work as explicit items with an ID, workspace, task, scope, assignee, dependencies, status, and result reference. Broad inspection and review are distinct assignments. Multiple unrelated reads may proceed concurrently; the initial release should preserve the existing limit of one editing worker per workspace.

Claim work atomically before dispatch. Only the current claim holder may complete that item. Record a run generation so a delayed result from a canceled or superseded run cannot overwrite current state. Commit the claim before starting the provider call; never hold a database transaction open while a model works.

Stop/crash recovery marks incomplete work accurately and handles owned processes before reassignment. Expiration alone must not silently authorize a second live worker to edit the same scope. Agent status such as 'waiting for findings' should reflect real dependencies in the coordinator.

Implemented first-release default: a visible preferred inspector, with the other agent reviewing. A user addressing only one agent keeps targeted behavior. For a request to both, the preferred inspector owns discovery; fixed role selection does not imply fixed ownership of every future task. Codex is the default inspector, with an explicit Settings choice for Claude. Automatic selection can follow usage evidence. Both agents can participate without both receiving the same execution assignment.

### Reusable evidence and freshness

A finding records a concise statement, source paths or artifact references, producing task/agent, verification state, timestamp, and applicable workspace revision. Distinguish model-reported findings from facts directly established by recorded tools or host checks. Memory is task data, not authority to change user instructions or permissions.

The host checks repository state and relevant file fingerprints before reusing volatile findings. Include uncommitted changes and added/deleted files, not only the commit ID. File watching is an optimization; changes missed while the app was closed must still be detected. Folder-based projects without Git need file-based validation.

Durable project decisions and volatile observations have different lifetimes. Build/test evidence additionally records the command, exit status, relevant code/dependency state, and environment assumptions. A previously passing test is not a statement about all later code.

Use explicit task kinds and scopes for reusable operations such as project status. Exact prompt-string matching alone is not a useful definition of duplicate work. Start with a few clear operations rather than promising general semantic deduplication of arbitrary requests.

### Persistence and worker reports

A local SQLite store under AI Hub's application data is the recommended durable target for multiple task records and atomic claims. The first status-only slice may retain the existing atomic JSON storage for one latest report if ownership is enforced by one coordinator process; it must not claim cross-process exclusivity without a process-wide or database-backed mechanism. Keep each workspace/working tree logically separated. AI Hub owns writes and validates structured reports; workers do not race to rewrite one shared Markdown file. Use short transactions for claims and updates when the database is introduced. Human-readable exports can be derived from the store.

An assignment brief includes the user's objective, the worker's scope, relevant project/task facts with their freshness, open questions, and expected result fields. A completion report contains a summary, evidence, changed files, tests actually run, unresolved items, and proposed reusable findings. A malformed or unsupported report must not mark work verified or complete.

The first implementation can pass briefings and collect reports through the existing adapters. Dedicated query/publish tools can follow when workers need selective mid-task access. Preserve source artifacts outside the prompt and retrieve relevant excerpts as needed.

Each provider still consumes its own context. The objective is less repeated discovery and transcript injection, not zero-cost shared model state. Record actual usage where supplied, including cached input separately when available; do not label missing usage as zero.

## Example: project status

1. The user selects a project and requests its status. Selecting a folder alone does not start model work.
2. The hub checks whether a current project summary exists.
3. On a cold or stale project, one worker inspects the relevant scope and publishes findings with evidence. Skip generated output and dependencies when they are irrelevant; honor project instruction files.
4. The second worker receives the findings and a specific review question. It may inspect targeted source to verify a conclusion.
5. The hub displays the report and stores reusable findings.
6. An unchanged repeated request reuses current evidence. Changed scope triggers a targeted refresh, not an automatic full inspection by both workers.

## User interface

Show the owner, scope, progress, dependencies, and result of each active work item. Keep a compact project-memory view with source links, freshness, corrections, and an explicit refresh action. Display meaningful events such as 'using project summary', 'waiting for inspection', and 'reviewing test evidence'. Retain Stop all, per-agent addressing, permission approvals, and bounded collaboration.

Archive/delete conversation functionality is implemented. Deleting a conversation clears the latest project-status report if that conversation authored it; the confirmation states this. Archive keeps the report. Provider-native history, project files, and copies already displayed in other conversations remain outside that deletion. Future task records need an explicit retention policy as they are introduced.

## Implementation order and acceptance

1. Project-status slice: explicit inspection/review ownership, bounded reports, one latest status artifact per workspace, and reliable freshness checks. Give the reviewer the claims, supporting paths, and a specific verification scope. Include per-task provider session identity where it is needed to isolate this operation from unrelated work.
2. Replace repeated full chat injection with relevant context updates; generalize the store only when additional task kinds need more than a latest-report lookup.
3. General task dependencies, background-worker lifetimes independent of the selected conversation, recovery, visible ownership, and a project-memory inspector.
4. Tune retrieval and scheduling from measured usage and quality; expand parallel editing only with a defined conflict strategy.

Acceptance checks: one assigned broad inspection for two participating agents; reuse on an unchanged repeat request; refresh for edited/new/deleted files and branch changes; correct behavior without Git; independent task context; stale completions rejected; Stop/crash recovery; missing or conflicting evidence visible; and useful review retained. Compare actual provider usage on the same representative tasks before and after the change.

## Independent Claude Code review

The user requested a review by the installed Claude Code CLI. The full response is saved in [CLAUDE-MEMORY-REVIEW.md](CLAUDE-MEMORY-REVIEW.md). It reviewed the supplied brief with tools disabled; it did not inspect the repository or implement changes.

Accepted recommendations: reduce the first release to the status operation, keep one latest report instead of a general retrieval system, assign visibly distinct inspection/review scopes, use a deterministic initial role rule, and measure input/cache/output usage on representative requests before setting a numerical savings target. Decide deletion retention before persistent shared findings ship.

Corrections and limits:

- Commit, branch, and the set of dirty paths do not establish freshness: an already-dirty file can change again without changing that set. Size and modification time are useful hints, not proof of identical content. Validate supporting content or conservatively refresh when freshness cannot be established. Start with whole-report invalidation rather than a fine-grained dependency engine.
- Current tool events are not a complete file-access audit, particularly for shell commands. Report actual provider usage and known coordinator-assigned inspections; label incomplete read instrumentation rather than presenting exact read counts.
- Restarting a native session on every code edit would lose useful context and increase cost. Use explicit new tasks, incompatible workspace/configuration changes, or a controlled context reset as session boundaries; send ordinary source updates as evidence-backed task changes.
- Claude's proposal to retain derived project findings after deleting their source conversation is an option, not an approved retention policy. The UI must disclose retained knowledge and provide removal before that behavior is adopted.

Reference: [Microsoft.Data.Sqlite transactions](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions).
