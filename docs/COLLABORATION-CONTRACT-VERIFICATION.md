# Collaboration contract and connection slice

Historical development milestone, prepared September 27, 2026. At this slice the installed app was v0.6.0 and the desktop did not yet enable these tools. See [0.7.0 release verification](COLLABORATION-RELEASE-VERIFICATION.md) for current desktop and installation status.

This document records the original contract/connection slice. The subsequent [durable storage and coordinator-routing milestone](COLLABORATION-ROUTING-VERIFICATION.md) adds a separate persistent backend. The isolated probe described below remains available for connection testing.

## Implemented

- A shared C# submission/content/envelope model for `handoff`, `review_request`, `review_result`, `question`, and `status` messages.
- A version 1.0 [published JSON Schema](collaboration/submission.schema.json). The MCP tool and runtime validator use the same schema definition. Host envelope fields cannot be supplied through the submission schema.
- Closed, bounded, type-specific payloads; duplicate-field, invalid-version, unknown-field, invalid-path, oversized-input and malformed-JSON rejection. Additional host checks validate identity, ownership, reply relationships, finding IDs and path aliases. Encoded submissions are limited to 32 KiB and 16 levels of nesting.
- Explicit allowed delivery-state transitions. A delivery record cannot jump from accepted to answered or revive a terminal state. Actual scheduling and persistence are reserved for the next milestone.
- An isolated in-memory `CollaborationProbe`, bound to one real `TaskMemory` claim. It exposes `get_task_context` and `submit_message`, rejects stale owners/generations, and gives identical retries the same receipt. A changed payload cannot reuse its idempotency key.
- A stdio MCP bridge backed by a same-user named pipe. A separate, random credential binds each connection to its host-selected provider and dispatch. The native provider session is bound before tool calls are accepted. Revocation closes the pipe; credentials are passed through the child environment, not tool arguments or message records.
- Optional adapter configuration for Codex app-server and Claude stream JSON. Existing adapter behavior remains when no collaboration host is supplied. Claude receives only the configured Hub MCP server for the probe, and only the two known Hub tool names receive automatic metadata-tool approval.

Both adapters use the same MCP server contract. The bridge never writes a task store, runs commands, edits project files, or launches a peer model. Receipt content explicitly says `persistent: false` and `automatic_dispatch: false`.

## Validation

| Check | Observed result |
| --- | --- |
| Release solution build | Passed, zero errors; NU1900 audit-feed warning noted below. |
| Full regression executable | 103 tests passed. |
| Independent JSON Schema validation | All 23 fixtures matched their expected result. |
| Real Codex and Claude MCP checks | All six cases passed, including native session resume. |
| Isolated live workspace | Remained empty after the checks. |

The independent schema corpus includes all five message types, valid review findings, Unicode string lengths, integer values encoded with a decimal point, invalid routing fields, missing required fields, unsupported versions, path traversal, and size bounds. The C# validator and Python `jsonschema` validator evaluate the same 23 cases. UTF-8 byte limits, duplicate JSON keys, depth, ownership, and reference checks are additional runtime tests; ordinary JSON Schema does not express all of these transport/host rules.

The regression runner covers the original 96 tests plus seven collaboration groups: contract rejection, state transitions, idempotency/evidence claims, stale ownership/cancellation, peer references/path aliases/immutable reads, real bridge transport/revocation/repair limits, and authentication/native-session binding. It also checks the published schema against the runtime tool schema and rejects malformed RPC parameters without losing the connection.

Real-provider checks use an isolated application data profile and an empty workspace. A random marker is present only in the saved task objective. Each agent must retrieve it through `get_task_context` and submit it through `submit_message`; the host inspects the actual recorded message and provider-session binding rather than trusting the final prose reply. Cases cover fresh and resumed read-only sessions, then fresh editing-mode sessions for both providers. Editing mode is exercised without requesting project edits. The workspace must remain empty.

Installed CLI versions for this run: Codex 0.157.1 and Claude Code 2.1.283. These checks establish compatibility with those versions, not arbitrary future or older CLIs.

Evidence files:

- `artifacts/collaboration-build-results.txt`: full solution build output.
- `artifacts/collaboration-core-results.txt`: final regression output.
- `artifacts/collaboration-live-results.txt`: final native provider check output.
- `artifacts/collaboration-live-20260927-final/results.json`: host-observed envelopes, received payloads, provider replies, and tool/usage events.
- `docs/collaboration/contract-fixtures.json`: the shared schema corpus.

The build succeeded with a NuGet NU1900 warning because the existing dependency vulnerability feed was unavailable. No production NuGet dependency was added. Python `jsonschema` and its dependencies were downloaded only under `artifacts/schema-validation` for the independent validation check.

The restricted shell could not connect child processes to same-user named pipes. Transport/regression/native checks were run outside that shell sandbox. The providers' own read-only/editing permission modes stayed enabled; no bypass-permissions mode was used. This establishes the local Windows desktop transport, not support for a bridge launched inside an arbitrary external sandbox.

## Reproduce

From the workspace root, build first:

```powershell
dotnet build 'AI Hub.slnx' -c Release -m:1 -p:UseSharedCompilation=false
```

Run the executable directly for the full suite: its subprocess fixtures relaunch `Environment.ProcessPath`, so invoking the DLL with `dotnet` is not equivalent.

```powershell
& '.\tests\AIHub.Tests\bin\Release\net10.0\AIHub.Tests.exe'
& '.\tests\AIHub.Tests\bin\Release\net10.0\AIHub.Tests.exe' --collaboration-tests
```

Export the schema/fixtures after intentional contract changes, and check schema parity:

```powershell
& '.\tests\AIHub.Tests\bin\Release\net10.0\AIHub.Tests.exe' --collaboration-export docs\collaboration
python -m pip install --target artifacts\schema-validation jsonschema==4.26.0
python tests\validate_collaboration_schema.py
```

The live test makes real model calls using the installed CLIs and their existing sign-ins. Choose a new output path each time. Other tool requests are denied by the test host.

```powershell
& '.\tests\AIHub.Tests\bin\Release\net10.0\AIHub.Tests.exe' --collaboration-live artifacts\collaboration-live-new-run
```

## Remaining boundaries

Probe messages do not survive process exit and never trigger a peer. There is no durable delivery queue, automatic structured routing, evidence backend, snapshot capture, completion certification, plugin package, or new desktop card. Evidence references are rejected in the probe rather than accepted without a backing record. A finding's disposition and a status of `assignment_complete` remain agent claims. The probe does not promote them to verified state.

Cancellation/ownership and authentication tests are local fixtures. Native checks establish successful tool calls and resume; they do not establish the later end-to-end implement/review/fix workflow. Existing conversation behavior is covered by the regression suite. No new desktop user flow was added or visually certified in this milestone.

Next: implement atomic storage for each task's messages, receipts and delivery states, then route validated requests through `HubCoordinator` after successful turn completion. Enforce user-selected participants, automatic-exchange settings, bounded repair, terminal statuses, and restart recovery there before enabling the tools in the desktop.

## Protocol references

The connection setup was checked against the installed CLI help and the official [Codex app-server documentation](https://learn.chatgpt.com/docs/app-server), [Codex MCP configuration](https://learn.chatgpt.com/docs/extend/mcp), and [Claude CLI reference](https://code.claude.com/docs/en/cli-reference). The bridge negotiates the [MCP 2025-03-26 lifecycle](https://modelcontextprotocol.io/specification/2025-03-26/basic/lifecycle) and implements its [tools protocol](https://modelcontextprotocol.io/specification/2025-03-26/server/tools).
