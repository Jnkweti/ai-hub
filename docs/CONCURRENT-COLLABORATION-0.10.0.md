# Concurrent collaboration 0.10.0

The selected first speaker and the waiting agent now receive the same frozen
initial common task context. The waiting agent prepares bounded tentative notes
without execution tools. Speaking stays ordered: its normal speaking turn gets
the first response, updated common context, and its own tentative notes. It
reconciles those inputs and contributes only useful additions or a quiet pass.
Preparation continues into the same native session when the provider supports
resumption. The host records its assignment, exact input, and outcome.

Preparation is limited to two minutes and 6,000 characters. It cannot submit
task messages or acquire execution ownership. Approvals are denied, Codex tool
features are disabled for that process, and Claude receives an empty tool list
and empty strict MCP configuration. An unexpected tool event invalidates the
preparation. These are adapter restrictions, not a separate OS isolation layer.
Feature configuration follows the [official Codex configuration reference](https://developers.openai.com/codex/config-reference/).

Stop, correction, failure, blocked work, a completed simple answer, and a research
split cancel and join unused preparation before releasing task ownership.
Preparation failure falls back to the normal current-context peer turn. Explicit
single-agent requests have no peer preparation. A correction preserves the
interrupted speaker; explicit addressing takes precedence.

## Sharing work

`claim_work` atomically records task-bound ownership or returns a matching result.
`complete_work` publishes the owner's bounded summary and captured evidence IDs.
`get_work` pages historical records. The host binds claims to the current agent,
dispatch, generation, scoped inputs, and environment identity. Claims are durable,
limited to 128 per task, and interrupted on abandonment or restart. Nothing
replays automatically. Existing 0.9 task records receive empty work collections.

Discovery reuse checks scoped file hashes. Check reuse additionally requires the
same live generation, whole-workspace fingerprint, host environment hash, exact
command, known successful exit, and stable native evidence captured before and
after execution. Hashes do not establish the stability of external services,
ignored dependencies, runtime state, or unknown inputs: callers must declare
such work nonreusable. Deliberate independent verification bypasses reuse and
retains existing review-evidence requirements. Summaries remain agent claims.

Operation matching is intentionally exact. Codex can record a full PowerShell
wrapper instead of the submitted script. When those differ, the original claim
cannot use that evidence; the agent can publish under the exact recorded
invocation and peers can reuse that record. A caller using the bare script will
not match the wrapper record. This conservative limitation avoids treating
different commands as equivalent based on agent assertions.

Main execution remains serialized; split researchers already have disjoint
host-validated scopes. The work registry coordinates agents that follow its
protocol. Arbitrary native calls bypassing that protocol cannot be transparently
deduplicated. Native provider permissions remain in force.

Research findings enter continuation prompts automatically. Workers retrieve only
needed omitted detail. Both researchers count as contributors, so one synthesis
ends that phase unless a concrete peer request remains. Identical scoped hash
requests share a capture within each context rendering operation; captures never
cross execution or review boundaries.

Shared context and exports include preparation assignments and shared work
ownership, summaries, and evidence references. Activity distinguishes preparation
from speaking. The three bundled workflow skills use the same protocol.

## Verification

The release build completed with zero warnings/errors. All **160 regression
tests passed**, including concurrent preparation/order/continuation, cancellation,
correction routing, exclusive recipients, work ownership/evidence/reuse, scoped
invalidation, and multiple authenticated bridge connections with revocation.
Evidence: `artifacts/collaboration-010-build.txt` and
`artifacts/collaboration-010-tests-release-final.txt`.

Both native preparation directions passed with the same frozen initial common
hash and same-session resumption. A short answer appeared once. The first run
exposed Codex's second MCP connection on resume; bounded concurrent connections
fixed it. Explicit disconnection then fixed process revocation in the protocol
test. The final prompt also explicitly omits `reply_to` when no incoming peer
message exists. Discussion evidence is in
`artifacts/collaboration-010-native-discussion-final`; this run preceded those
last cleanup/prompt adjustments, which passed final regression and the native
work/research checks below.

The final real-provider shared-work test captured one successful Codex command
and Claude's actual `claim_work` receipt with disposition `reused`, with no
Claude command execution. Native parallel research overlapped, saved both
findings, and completed one synthesis without an extra general peer turn.
Files remained unchanged. Evidence: `artifacts/collaboration-010-native-work-final`
and `artifacts/collaboration-010-native-research`.

The small research fixture took 62.22 seconds / 5 calls / 38,571 host-input bytes;
the single-agent comparison took 39.66 seconds / 1 call / 6,652 bytes. These runs
include provider/network variability and do not establish a speed or cost gain.
Host bytes exclude provider system instructions, tools, and native history.

Final desktop checks passed for task switching, stop, persistence, shared
findings, exact inputs, pin/supersede controls, and an upgrade using a copied
production profile. No model work started during profile upgrade, and live
production data remained unchanged. Logs: `artifacts/collaboration-010-desktop-final.txt`,
`artifacts/collaboration-010-context-ui.txt`, and `artifacts/collaboration-010-upgrade.txt`.
The schema's 26 fixtures, plugin manifest, and all three bundled skill validators
passed. Core DLLs in the test runner, release desktop package, and packaged bridge
match SHA-256 `FBCE71D63E102A12954FD50EA0B1340B222F2CC3286C0D7A9AAB13FB0C8D765C`.

The new local Git repository records source checkpoints separately from release
verification. See [the Git workflow](GIT-WORKFLOW.md).

Installed **0.10.0.0** after confirming the previous app was idle and closing it
normally. All 611 package files matched the candidate, and all eight production
profile files were unchanged. App/data backups are
`artifacts/before-collaboration-20260927-103908.zip` and
`artifacts/before-collaboration-data-20260927-103908.zip`. Installation and normal
profile reopen evidence are in `artifacts/collaboration-010-install-result.json`
and `artifacts/collaboration-010-reopen-result.json`.
