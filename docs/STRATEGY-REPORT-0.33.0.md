# Strategy report — 0.33.0

The place where the learning plan's Phase 5 comparison lands as feedback accumulates. It reads the feedback store and
the shadow log and renders Markdown; it executes nothing and learns nothing.

## Contents

| Section | What it shows |
| --- | --- |
| Feedback by strategy | For reaction rounds and for independent answers: useful, needs-correction and preferred-alternative counts, the useful rate, and progress toward the five judgements the shadow policy needs before it uses feedback. Records without a strategy (before 0.32.0) are counted separately. |
| Aspects named in feedback | Per aspect (correctness, relevance, explanation, scope, initiative, collaboration, effort): useful / needs-correction counts under each strategy. |
| Shadow decisions | Phases observed, how often the policy would have chosen what ran, per executed strategy, and the twenty most recent disagreements with the task, phase and the policy's reason. |
| Reading this | Feedback is counted under the strategy setting in force when it was recorded; agreement says nothing about outcomes; the comparison does not control for task kind. |

Reached from the Tasks window ("Strategy report"), with Save as Markdown.

## Verification

- Build: zero warnings, zero errors. Full regression suite: **275 tests passed** — one new case renders a report from
  synthetic feedback and shadow decisions and checks the per-strategy table, the unlabeled count, the aspect table,
  the shadow summary per executed strategy, the labeled disagreement list, the caveats, and the empty report.
- No UI check was run (see [quiet UI checks 0.32.1](QUIET-UI-CHECKS-0.32.1.md)); the window is a read-only text
  box with a save button built the same way as the review-packet viewer.
- Package `artifacts\strategy-report-0330-release` (0.33.0.0) installed on October 3, 2026
  (`artifacts\strategy-report-0330-install-result.json`: 612 files verified, hashes match, data unchanged, backups
  `before-collaboration-20261003-203002.zip` and `before-collaboration-data-20261003-203002.zip`); the app was left
  closed and no window was opened.
