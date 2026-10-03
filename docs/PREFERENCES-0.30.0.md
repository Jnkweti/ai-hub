# Preference memory — 0.30.0

Phase 4 of [EMERGENCE-AND-LEARNING-PLAN.md](EMERGENCE-AND-LEARNING-PLAN.md): persistent adaptation through context,
under the developer's control. No learning, no inference: every preference exists because the developer wrote it or
confirmed it from a feedback record.

## What it does

| Surface | Behavior |
| --- | --- |
| Preferences window | Reached from the Tasks window. Lists every preference with its scope, version, origin and state; New, Edit, Enable/Disable, Delete, Export all. The notice says that changes apply from the next phase and that a running or resumed native session may still hold the earlier text. |
| Make preference | In All feedback, turns the selected feedback record into a preference draft (the explanation as text, the feedback's scope), which the developer edits and saves. The preference keeps the feedback's id as support; deleting that feedback disables the preference with a reason, until the developer restates it. |
| Supply | For each task phase, the host asks the store for enabled preferences in scope — general ones, the project's, the task's own — and appends them to the common task context after the active user instructions, under a heading that states they rank below every instruction above and any newer user message. At most twelve preferences or 6,000 bytes are supplied; the rest are listed as omitted. |
| Record | Each supplied preference appears in the common context's included ids as `pref:<id>:v<version>`, so the existing input manifests record exactly which version each agent received; editing a preference's text or scope raises its version. |
| Tasks window | The selected task's details list the preferences that will be supplied on its next phase. |

Category-scoped preferences ("this kind of task") are stored and shown but not supplied automatically: tasks carry no
category, and guessing one from the objective would be inference. They are a hook for the next stage, not a feature.

## Precedence

Active user instructions and pinned corrections come first in the common context; preferences come after them with
an explicit lower-precedence note; the current user message is repeated last. A newer message therefore always wins
over a preference without any host logic to resolve conflicts, and a one-time correction never becomes a preference
unless the developer makes it one.

## Verification

- Build: zero warnings, zero errors. Full regression suite: **268 tests passed** — four new cases: versioning on
  edit, scope relevance (general, project, task; category not auto-applied; nothing leaks across projects or
  tasks), disable rather than widen, restart and delete; disabling a feedback-derived preference when its feedback is
  deleted while a stated one is kept; repair of a damaged file (invalid records dropped, anchorless scopes disabled,
  backup made); and the coordinator path: the preference block appears below the instructions with its precedence
  note, the input manifests record `pref:<id>:v1`, the supply budget holds, and an empty set produces no block.
- Desktop: `tests\Feedback-Smoke.ps1` now also makes a preference from the recorded feedback (checking the draft
  starts from the explanation and the saved record links the feedback, scope and project), opens Preferences from
  the Tasks window, finds it, disables it and confirms the file (version unchanged by disabling).
- Live acceptance (October 3, 2026, `artifacts\pilot-028\pref-in` and `pref-out`): the first pilot task, Claude Code
  alone, run twice from the same state with one preference — "End every reply with a final line that starts with
  'Confidence:' (high, medium or low) followed by the single most important assumption you did not verify" — scoped
  to the pilot project in one run and to a different project in the other. In scope: the common context carried the
  block, the input manifest records `pref:56d97d5b…:v1`, and the reply ended "Confidence: high. The unverified
  assumption is that the customer's report was produced from this exact `data/week.csv` …" (1 min 49 s, $0.55). Out of
  scope: no block in the context, no `pref:` id in the manifest, and no such line (1 min 16 s, $0.61). Both replies
  were otherwise equivalent and correct. One pair; it shows that a preference changes behavior in scope and not
  outside it, not that preference memory improves outcomes.
- Package `artifacts\preferences-0300-release` (0.30.0.0) passed the three smoke checks (the feedback smoke's first
  run failed on a PowerShell 5.1 quirk in the check itself — a one-element JSON array reads back as a string — fixed in
  the script); installed on October 3, 2026 after closing the idle 0.29.0 app
  (`artifacts\preferences-0300-install-result.json`: 612 files verified, hashes match, data unchanged, backups
  `before-collaboration-20261003-181111.zip` and `before-collaboration-data-20261003-181111.zip`); reopened as
  0.30.0.0 with 5 rooms and 2 tasks.
