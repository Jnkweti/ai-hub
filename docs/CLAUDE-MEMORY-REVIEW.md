# Claude Code review of shared memory and task ownership

Received September 24, 2026 from the installed Claude Code CLI. Reported model: `claude-fable-5-1`.

This was an independent review of the supplied design brief, with built-in tools disabled and no repository scan. It is advice about a proposal, not verification of an implementation. Raw request and response are in `artifacts/claude-memory-review-20260924-220733/`.

---

I'll review the proposal as written, without touching the repository.

**Overall verdict.** The diagnosis is right and the direction is right. Duplicate discovery comes from both workers receiving the same broad assignment plus the same transcript, and no code path records who owns what. Fixing that is a scheduling problem, not a memory problem. The proposal's main risk is that it bundles a full knowledge system, a freshness engine, and a task registry into the first release, when the token waste can be cut with a much smaller change.

## What I agree with

- **The hub as owner of scheduling and writes.** Workers should never race on a shared Markdown file. AI Hub validating structured reports is the correct trust boundary.
- **Ownership before dispatch, run generations, and short transactions.** These are the parts that survive cancellation and crashes. Keep them exactly as written.
- **Distinguishing model-reported findings from host-established facts.** This is the single most important line in the memory section. Without it, project memory becomes a store of confident guesses.
- **Binding provider sessions to task rather than room.** Reusing one long native session across tasks silently carries old context. The proposal identifies this correctly.
- **Not defining duplicates by prompt-string matching.** Named task kinds with explicit scope are the only workable definition.

## Highest-risk flaws and overengineering

**Freshness by file fingerprint is a larger project than the proposal admits.** Detecting uncommitted changes, added and deleted files, branch switches, and changes made while the app was closed means the hub effectively maintains its own content index of the working tree. Assumption: the workspaces are typical repositories with thousands of files. Hashing them on every status request costs real time, and a partial hash is a correctness hole. My recommendation is to shrink freshness in the first release to a coarse signal. Record the commit ID, the set of dirty paths from git status, and the branch. If any of those changed, treat the summary as stale and refresh. For non-Git folders, use a manifest of relative paths with size and modification time. Accept that this is conservative. A conservative staleness check wastes a refresh. A permissive one serves wrong answers, which is worse.

**Project memory as a selected briefing is retrieval, and retrieval is where these systems fail quietly.** The proposal says memory is "selected into a compact briefing" but never says how. If selection is by another model call, the cost moves rather than disappears. If it is by task kind, it only works for the few named operations. I would state this openly: in release one, the only retrievable artifact is the current project summary for the status task. General reusable findings with provenance should wait until there is a consumer that needs them.

**The reviewer role can become theater.** Step four gives the second worker the findings and a review question. If the reviewer's default behavior is to re-read the same files, nothing is saved. If the reviewer is told not to read, it cannot verify anything. The brief must make the reviewer's scope explicit in both directions: here are the claims, here are the paths that support each claim, confirm or refute by reading those paths and any file they directly reference. Bound the reviewer to targeted reads by listing them, not by asking it to be economical.

**Automatic owner selection is underspecified and I disagree with making it a default.** The proposal says the policy is still a preference to confirm. Shipping an automatic policy that nobody has agreed on invites the exact complaint the user has now, only less predictable. Start with a fixed rule that is visible in the UI: the agent the user addressed inspects, the other reviews. Add automatic selection once there is usage data to justify it.

**Measuring usage is listed as a goal but has no acceptance number.** "Compare actual provider usage before and after" is not a check. Pick two or three representative status requests, record input and cached-input tokens per provider today, and set a target reduction before implementing. Otherwise the project cannot fail, which means it also cannot succeed.

## Smallest useful first release

- **Add task kinds for exactly one operation: project status.** Everything else keeps the current dual-dispatch path.
- **Replace the shared broad prompt with two distinct briefs.** One inspection brief scoped to the workspace. One review brief containing the inspection report, the paths it cites, and one question.
- **Store only the latest status report per workspace** with commit ID, branch, dirty paths, and a timestamp. No general finding store yet.
- **Add a run generation and a single-claim check** so a canceled inspection cannot write a stale report.
- **Show two UI events**: "inspecting" and "reviewing findings from Codex" or similar. Show a "refresh" button that invalidates the stored report.

This removes the duplicate discovery on the exact request the user complained about. It also exercises the claim, report validation, and staleness paths before they carry more weight.

## Avoiding duplicate reads without suppressing verification

Verification should be cheap and targeted, not forbidden. The mechanism is provenance in the report. Each claim in the inspection report carries the paths that support it. The reviewer's brief permits reads of those paths and states that reading beyond them requires a stated reason in the completion report. The hub then displays how many files each worker read. Assumption: both adapters expose file-read events or at least token usage per turn. If they do not, this measurement is the first thing to add, because the user cannot otherwise tell whether the review is independent or redundant.

## Boundaries, invalidation, ownership, and deletion

- **Session boundary**: bind the native session to task and permission mode, as proposed. Also expire it when the workspace revision changes materially, or the worker's own history becomes a second stale memory that the hub cannot inspect.
- **Invalidation**: make staleness a host decision, never a worker decision. A worker may report "this seems outdated," but only the hub marks a record stale.
- **Ownership on crash**: the proposal's "expiration alone must not authorize a second editor" is right. Add that recovery must show the user the orphaned item and require an explicit reassign for edit-scope work. Read-scope work can be reassigned automatically.
- **Conversation deletion**: decide now, not later. My recommendation is that deleting a conversation deletes task records created from it but retains project-level records, marked with a "source conversation deleted" flag and a visible delete action of their own. Retaining derived findings invisibly would surprise a user who deleted the conversation to remove its content.

The proposal is sound as a target architecture. Ship the status slice with coarse freshness and a fixed role rule first, and let the rest earn its way in through measured token savings.
