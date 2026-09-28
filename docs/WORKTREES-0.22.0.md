# Per-agent worktrees — 0.22.0

The last item of batch 3 in `docs/TODO.md`, from agent-bridge-mesh and rennerdo30/agent-bridge: with the setting on,
each agent edits in its own git worktree and the host merges into an integration branch. Your project folder changes
only when you choose to merge. This removes the shared-file race between two agents editing one tree.

## How it works

- Applies when Settings → "Give each agent its own git worktree" is on, the room allows edits, and the project is a git
  repository with at least one commit. Otherwise nothing changes.
- At the first phase of a task, `GitWorktrees.EnsureAsync` creates three worktrees under `%LOCALAPPDATA%\AIHub\wt\<task>`
  from the project's HEAD: `c` (Codex, branch `aihub/<task>/codex`), `k` (Claude, `aihub/<task>/claude`) and `i`
  (integration, `aihub/<task>/integration`). Later phases reuse them. The layout is saved in the task ledger and shown
  in the Tasks window.
- Each agent's CLI runs with its own worktree as its working directory, and its assignment says so. Before an agent's
  turn, the integration branch is merged into its worktree so it sees the teammate's committed work. After a
  successful turn, the host commits everything the agent changed on its branch and merges that into integration.
- Snapshots, evidence freshness, research scopes and the review packet read the integration worktree, so "current
  files" means the merged result.
- Conflicts are never resolved silently. A conflicting merge is aborted, the conflicting files are named in a system
  event and an error notice, and the affected worktree keeps its own versions; one agent must reconcile explicitly.
  In the ordinary sequential flow conflicts do not arise, because each agent starts its turn from the current
  integration state.
- Tasks → "Merge into project" merges the integration branch into your checkout (fast-forward when possible; a
  conflict aborts and names the files). "Remove worktrees" deletes the worktrees and branches; unmerged history is lost,
  and the button says so.

## Verification

- Build: zero warnings, zero errors.
- Full regression suite: **239 tests passed**: the 237 of 0.21.0 plus two new cases in `WorktreeTests`, run against a
  real git repository at a short temporary path: both agents edit in their own worktrees, the second sees the first's
  committed file before its turn, the integration branch holds both files while the project checkout has neither, the
  stream records the layout and two merges, Merge into project brings both files, and Remove worktrees deletes the
  worktrees and branches; a conflicting publish and a conflicting sync are aborted and report `shared.txt`, leaving the
  worktree clean with its own version, while a clean sync brings integration into the other worktree.
- Package `artifacts\worktrees-022-release` (file version 0.22.0.0) passed `tests\Local-Diagnostics-Smoke.ps1` and
  `tests\Conversation-Management-Smoke.ps1` (all four conversation-management cases).
- Live provider check: not run; the Codex account is over its limit until October 3, 2026. Running the real CLIs
  inside worktrees is a live check for after that date, with the setting enabled.
- PENDING: clean checkout verification, installation.
