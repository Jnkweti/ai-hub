# Durable collaboration and routing milestone

Historical milestone report, prepared September 27, 2026. This records the storage/coordinator slice after the [contract and connection slice](COLLABORATION-CONTRACT-VERIFICATION.md). For current desktop, evidence, plugin, and installation status, see [0.7.0 release verification](COLLABORATION-RELEASE-VERIFICATION.md).

## Behavior

`CollaborationStore` saves one `collaboration-<task ID>.json` document per task through `LocalStore`'s atomic replacement. Each document holds the message, its idempotency key and payload checksum, sender-success flag, delivery state, delivery-dispatch reference, sequence number and interruption reason. A receipt is returned only after saving succeeds. A changed retry cannot reuse its key; an identical retry returns the same message ID. The checksum detects accidental content inconsistency; it is not an authenticity signature.

The application supplies identity, task, participants, run generation, dispatch and native-session binding. Tool inputs cannot change these. All store operations follow the lock order TaskMemory then collaboration store, and each tool call checks the current owner and dispatch. No peer can release a task claim or grant editing permission.

The store supports three tools: `get_task_context`, `submit_message`, and `get_messages`. History reads accept only a sequence cursor and page size, remain within the active task, and never schedule work. Pages are bounded by count and serialized size. An individual full message may exceed the page's soft size budget; the transport frame remains bounded. Notes and peer claims are labeled as historical context.

`HubCoordinator` opts into this workflow when its collaboration store, bridge path and provider factory are supplied. An incomplete configuration fails visibly without starting the legacy factory. Without those settings, the existing conversation path remains in use. The desktop has not been switched to the structured path yet.

Each structured dispatch gets its own revocable MCP connection and provider process. The provider factory can resume the previous native session. After a successful provider reply, the coordinator commits the terminal message, revokes that dispatch's tools, disposes the client, and releases speaker ownership before starting another speaker. The workspace editing claim stays held across the whole exchange.

Routing uses the validated terminal message; prose such as `Task complete.` or `Passing to Claude Code.` cannot override it. The current user-selected participants constrain recipients. With automatic collaboration off, a peer request is saved but no peer starts. Pending requests at the end of a run become interrupted history, requiring an explicit new user instruction and a fresh dispatch; they are not automatically replayed.

There is one terminal message per dispatch. Progress messages are permitted before it. A review request must receive a review result or an explicit blocked status. Terminal answers reference the incoming message ID. Missing terminal messages get at most two model repair turns, then a visible pause. Three tool-validation/storage errors or the tool-call bound also prevent routing. The existing configured round backstop applies; repair turns have their own bounded allowance. Structured mode does not use prose repetition heuristics to decide delivery.

## Delivery semantics and recovery

| State | Meaning |
| --- | --- |
| Accepted | Persisted, but the sender's provider turn has not committed successfully. |
| Pending | Sender succeeded; the coordinator may deliver if current user settings permit it. |
| Delivered | Assigned to a provider dispatch as incoming context, or received by the Hub as a successful status report. |
| Answered | A successful peer dispatch submitted a structured answer. Its conclusions remain agent-reported. |
| Interrupted | Failure, cancellation, pause, shutdown, restart, or a newer run ended eligibility for delivery. |

Delivery is recorded before invoking the provider so a crash or failure can be recovered conservatively. It records the host's dispatch attempt, not proof that the model received, retained, understood, or acted on the message. Failed attempts become interrupted. There is no exactly-once model execution guarantee across process or OS failure.

Accepted messages from failed sender turns cannot dispatch a peer. Failed acceptance does not return a receipt or update the in-memory document. Failed commit does not promote a handoff to pending. Startup marks unresolved saved messages interrupted and launches no workers. A fresh run cannot deliver an older generation's request, though it can retrieve it as historical context.

Task and collaboration documents are separate atomic files. A crash during cleanup or deletion can leave orphan history, but reads and dispatch require an existing task and current claim. Orphans are not restored into tasks. Startup validates supported versions, task/room/workspace identity, sequences, identifiers, payload schemas and checksums before using history. Unsupported/oversized history is preserved and rejected; malformed JSON follows the existing backup/recovery path.

Retention is deliberately simple: keep all messages and their references up to 512 records or an 8 MiB encoded document per task. Reject additional writes at the bound rather than silently prune referenced messages. Archive retains the task/history. `CollaborationStore.DeleteRoom` wraps task, collaboration-document and conversation deletion with rollback for ordinary failures; other rooms are unaffected. It must replace the desktop's existing task-delete call when structured mode is enabled. Cross-file crash-atomic deletion is not claimed.

## Validation and evidence

| Check | Observed result |
| --- | --- |
| Targeted durable storage/routing cases | All 16 passed. |
| Full regression suite | 119 tests passed. |
| Release solution build | Passed with zero errors and the NU1900 audit-feed warning noted below. |
| Real provider review round trips | Codex -> Claude -> Codex and Claude -> Codex -> Claude passed. |
| Explicit continuation | Both authors resumed native sessions and retrieved saved history successfully. |
| Recovery and workspace | Completed records survived restart; no work auto-started; the test project remained empty. |

The targeted tests cover atomic acceptance/commit failures, retry deduplication, stale owners/generations, restart without replay, isolated paginated history, unsupported/oversized history, deletion rollback, contradictory prose, review return routing, participant restrictions, automatic-exchange settings, missing terminal repairs, tool-repair exhaustion, round limits and cancellation.

Real-provider checks used Codex and Claude with the production coordinator and store in an isolated profile. Both role orders completed a three-dispatch review round trip. Each author then explicitly continued, resumed the same native session, retrieved saved messages, and recorded a new generation and sequence. Completed records survived store/application-memory reconstruction without automatic work. The test project remained empty.

This live test verifies communication and persistence. It does not certify actual code review quality, test assertions, evidence capture, source freshness, or the future implement/review/fix workflow. The live run preceded a pagination integer-normalization adjustment and extraction of the shared path validator; the subsequent regression suite covers those changes.

Evidence:

- `artifacts/collaboration-routing-targeted-results.txt`: targeted storage/routing cases.
- `artifacts/collaboration-routing-core-results.txt`: full regression suite.
- `artifacts/collaboration-routing-build-results.txt`: solution build, including desktop compatibility.
- `artifacts/collaboration-routing-live-results.txt`: native workflow outcomes.
- `artifacts/collaboration-routing-live-20260927-a/results.json`: persisted records and provider tool events for both role orders.
- `artifacts/collaboration-routing-live-20260927-a/data`: the isolated saved task and collaboration documents.

The solution build succeeded with zero errors. It retains the previously observed NU1900 warning because NuGet's dependency vulnerability feed was unavailable. No production package dependency was added. Native sign-in/session access and the existing named-pipe fixtures required execution outside the shell sandbox; the providers remained in their own read-only permission modes for this milestone's live test.

## Reproduce

```powershell
dotnet build 'AI Hub.slnx' -c Release -m:1 -p:UseSharedCompilation=false
& '.\tests\AIHub.Tests\bin\Release\net10.0\AIHub.Tests.exe' --collaboration-routing-tests
& '.\tests\AIHub.Tests\bin\Release\net10.0\AIHub.Tests.exe'
# Real model calls, using existing signed-in CLIs; choose a new output directory.
& '.\tests\AIHub.Tests\bin\Release\net10.0\AIHub.Tests.exe' --collaboration-routing-live artifacts\routing-live-new-run
```

The full regression suite must run through its executable, because its provider fixtures relaunch the current executable.

## Next milestone

Add the host-captured evidence store and snapshot-aware review records. Then connect structured mode, its deletion/recovery lifecycle, and readable message/review cards to the desktop, package the provider skills/plugins and bridge, and validate a complete candidate before installation. Evidence references currently fail validation because no evidence backend is connected. Status and review findings remain unverified agent reports.
