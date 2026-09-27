# Local Git workflow

AI Hub uses a local Git repository with `main` as its initial branch. No remote
repository is configured by this setup. Commits are local checkpoints; they do
not install the app or publish a release.

The initial checkpoint preserves the existing project, including the unfinished
0.10.0 collaboration preparation work. It is not a verified 0.10.0 release.
The installed 0.9.0 release and its verification are documented separately in
[SHARED-CONTEXT-0.9.0.md](SHARED-CONTEXT-0.9.0.md).

## Routine changes

Run these commands from the project folder:

```powershell
git status --short
git diff
git switch -c feature/descriptive-name
# Edit and run the checks appropriate to the change.
git add -- path/to/changed-file
git diff --cached
git commit -m "Describe the completed change"
```

Review the staged diff before committing. Source, tests, documentation, bundled
assets, and plugin definitions belong in Git. Build output (`app`, `artifacts`,
`bin`, `obj`), local environment files, credentials, and test profiles stored in
`artifacts` are ignored. Normal application data lives outside this repository
under `%LOCALAPPDATA%\AIHub` and is not backed up by Git. Ignore rules do not
replace checking new files for private data.

## Collaboration

Both agents in one checkout share its branch, index, and working files. Assign
file ownership before editing concurrently. Stage explicit paths, inspect the
entire staged diff, and serialize commits and branch changes through the task
owner. Never discard another agent's or the user's uncommitted work.

For work requiring separate branches and commits, use a separate worktree:

```powershell
git worktree add ../AI-Hub-review -b review/descriptive-name
```

Each worktree has its own checkout and index. Review its changes before merging
and run the relevant checks after integration. A Git worktree does not give the
agents shared live model context or replace AI Hub's task coordination.

## Reviewing history

```powershell
git log --oneline -10
git show --stat HEAD
git diff HEAD -- path/to/file
```

Use a reviewed `git revert <commit>` to undo a committed change while keeping
history. Publishing to a remote and release tagging are separate actions; choose
the destination and visibility before configuring a remote or pushing.
