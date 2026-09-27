# AI Hub usability review - September 24, 2026

All three rounds were implemented and checked in version 0.2.1. The original Enter-key and runaway-collaboration fixes are included in this release.

## Round 1: reliable messaging and useful collaboration

| Finding and root cause | Implemented improvement |
| --- | --- |
| Enter inserted a newline because the multiline WPF TextBox handled the key before the bubbling key handler. | Handle Enter in PreviewKeyDown; preserve native Shift+Enter and suppress repeated key submissions. |
| Every reply was relayed automatically, including greetings, agreement, and finished work. No host-level stopping rule existed. | Pause after greeting replies, when agents report completion or need human input, or when replies repeat without new tool activity. Add a six-round default backstop, configurable from 1 to 50 in Settings. |
| A round-counter increment inside an optional event invocation depended on a status listener being present. | Increment before notification; test enforcement without a subscriber. |
| A transient footer could hide the reason collaboration stopped. | Record a visible AI Hub message and a persistent pause banner with the reason. |

Checks: expanded regression tests and actual keyboard input. The real-provider hello test produced one reply from each provider and no agent-to-agent handoffs. One automatic round means a Codex turn followed by a Claude turn; the initial replies to the user are separate.

## Round 2: preserve work and explain recovery

| Finding and root cause | Implemented improvement |
| --- | --- |
| Composer text and recipient lived only in window controls, so switching rooms or restarting could lose them. | Persist each room's draft and recipient, and restore the last selected room. |
| A stopped conversation gave little guidance about what to do next. | Offer Write a message, and Continue task for manual stops, repeated replies, and round-limit pauses. Continue respects existing unsent text. |
| Empty submission and project permission state were unclear. | Disable Send for empty input; display a clickable READ ONLY or EDITS ENABLED indicator. |
| Settings gave advanced connection fields equal prominence and could make primary actions hard to reach in a small window. | Put collaboration and permissions first, collapse advanced options, keep Save and Cancel visible, and validate round limits. Bound connection checks with a timeout. |

Checks: room switching, restart persistence, draft-safe continuation, empty-send feedback, settings at 600 x 480, and invalid/valid round-limit entry.

## Round 3: navigate and read with less effort

| Finding and root cause | Implemented improvement |
| --- | --- |
| A growing conversation list had no search or rename flow. | Search names and project paths; rename with a button or F2; explain empty search results. Filtering preserves the active room. |
| Frequent actions depended on mouse navigation. | Add Ctrl+K search, Ctrl+L composer, Ctrl+N conversation, Ctrl+O project, Ctrl+, settings, and Esc to stop active agents. |
| Unconditional scrolling interrupted reading older replies. | Follow incoming messages only while near the bottom; show Latest messages when reading earlier content. |
| Copying had no immediate confirmation. | Show a temporary checkmark and Copied tooltip; report clipboard contention clearly. |

Checks: search, empty results, Escape, rename, shortcuts, copy feedback, and latest-message navigation. The final appearance pass checked the activity panel, window controls, compact layout, and reduced motion.

## Validation and remaining limits

All 28 automated checks passed. Final native UI checks and real Codex-Claude exchanges passed, including the exact greeting case. See [VERIFICATION.md](VERIFICATION.md) for evidence directories and coverage limits.

Completion and repetition checks are heuristics; the round cap provides a deterministic backstop. A pause explains its cause and leaves the user in control of whether to continue. Long-duration usage testing and exhaustive real-provider tool/approval coverage remain outside this release's verification.
