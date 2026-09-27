# Shared conversation correction (0.7.1)

The 0.7.0 host interpreted one agent's terminal status as the end of the entire shared exchange. Since new conversations started with Codex, Claude could remain silent unless a peer request or explicit user address selected it. The backend connection worked, but the default conversational behavior did not give both selected agents a turn.

The corrected host schedules one initial contribution for each selected participant. An agent completion ends that contribution, not the other participant's opportunity to respond. The host schedules the second contribution directly on the user's authority; it does not forge a peer request or reply-to relationship. Both receive the user message and shared conversation. Explicit peer requests still route through the durable request lifecycle.

Unaddressed follow-ups alternate who starts based on the previous successful run's first contributor, surviving coordinator recreation and application restart. Explicit names choose who starts; the Only selectors remain exclusive. Auto collaborate controls additional follow-ups after initial participation. Blocked messages, failed providers, cancellation, user-input waits, round limits, and editing ownership retain their existing safeguards.

Discussion instructions encourage direct engagement with the user, concrete additions or disagreement, and a brief pass when there is nothing useful to add. Ordinary discussion does not require ceremonial handoff language or a formal code review. Legacy standalone sign-offs such as "Task complete" and "Passing to Claude Code" are replaced by tool metadata. Routine status receipts stay in durable task history rather than appearing as extra chat cards. Greetings now receive a valid conversation/task context instead of trying to open a structured dispatch without one.

Verification artifacts:

- `artifacts/shared-conversation-build.txt`
- `artifacts/shared-conversation-core-results.txt`
- `artifacts/shared-conversation-live-20260927/results.json`
- `artifacts/shared-conversation-live-results.txt`
- `artifacts/shared-conversation-ui-results.txt`

This intermediate candidate was superseded by 0.8.0 before installation. The final release adds silent passes, exact-repeat suppression, and parallel shared context. See [0.8.0 verification](SHARED-CONTEXT-0.8.0.md) for the installed release and final checks.
