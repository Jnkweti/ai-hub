# Mid-turn push — 0.20.0

The first item of batch 3 in `docs/TODO.md`, from AgentBridge, rennerdo30/agent-bridge and hcom: a message you send
while Claude Code is working reaches it inside that turn instead of waiting for its next one. Codex has no equivalent
mechanism (its app-server takes input only at turn boundaries), so it keeps seeing your messages at its next turn.

## How it works

- The host's MCP pipe server can now send JSON-RPC notifications to an initialized connection
  (`CollaborationMcpHost.PushAsync`). Responses and pushes on a connection share one write gate.
- When mid-turn push is enabled and Claude Code is the current speaker, `HubCoordinator.InterjectAsync` pushes
  `notifications/claude/channel` with the user's message (marked as carrying user authority) to that session's
  connection, then records the message in the stream as before. The activity feed shows "Your message was delivered to
  Claude Code mid-turn". Nothing else changes: the turn loop still processes the interjection at the turn boundary and
  the message also appears in the next delta prompt.
- Claude Code is started with `--dangerously-load-development-channels server:ai_hub`, which loads the AI Hub MCP
  server as a channel, only when the setting is on. The flag is a Claude Code research preview; it is accepted by
  2.1.284 but not listed in its help.
- Settings → "Deliver my messages to Claude Code mid-turn (experimental)". Enabling runs `claude <flag> server:ai_hub
  --version` first and refuses if the build rejects the flag, so a structured turn can never fail to start because of it.
  Changing the setting restarts the provider connections.

## Verification

- Build: zero warnings, zero errors.
- Full regression suite: **235 tests passed**: the 233 of 0.19.0 plus two new cases in `HardeningTests`: a push with
  no connection delivers nothing, a push after the bridge client initializes arrives as a JSON-RPC notification with
  the content and tool calls still work afterwards; an interjection during Claude Code's turn is pushed with the
  user-authority marker only when the setting is on, and is recorded in the stream either way.
- Flag probe on this machine: `claude --dangerously-load-development-channels server:ai_hub --version` on Claude Code
  2.1.284 printed the version with no error.
- Package `artifacts\midturn-push-020-release` (file version 0.20.0.0) passed `tests\Local-Diagnostics-Smoke.ps1` and
  `tests\Conversation-Management-Smoke.ps1` (all four conversation-management cases).
- Live provider check: not run; the Codex account is over its limit until October 3, 2026. The end-to-end effect
  (Claude Code acting on a channel message mid-turn) is a live check for after that date, with the setting enabled.
- Clean checkout of commit `0b61be8` at a short temporary path built with zero warnings and passed all 235 tests
  against its own packaged bridge.
- Installed on September 28, 2026 after the idle 0.19.0 app was closed (no owned or running tasks, no child
  processes): `artifacts\midturn-push-020-install-result.json` (612 files verified, package hashes match, 10 profile
  files verified, production data unchanged, backups `before-collaboration-20260928-190852.zip` and
  `before-collaboration-data-20260928-190852.zip`). Reopened as 0.20.0.0 with 4 rooms and 1 task intact:
  `artifacts\midturn-push-020-reopen-result.json`. The setting ships off; enable it in Settings to use it.
