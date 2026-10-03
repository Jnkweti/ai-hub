# Pilot baseline — 0.28.0

Phase 0 and the first Phase 2 trial of [EMERGENCE-AND-LEARNING-PLAN.md](EMERGENCE-AND-LEARNING-PLAN.md): one representative,
unprescribed task run through AI Hub with both agents and through each provider alone from the same initial state. The
ground truth and the rubric below were written before any run was read. One task and one trial per arm is directional
evidence only; it cannot establish that collaboration is better than solo work.

## Pilot task

Workspace: a small Python project (`ledger`, 8 files, ~160 lines) at `%LOCALAPPDATA%\Temp\ah-pilot\ledger`, created for
the pilot and not committed, so nothing in any repository documents its bug. Read-only task; the providers run with
edits disabled (Claude Code in plan mode, Codex in a read-only sandbox). The prompt (`artifacts\pilot-028\prompt.txt`):

> Customers report that `python -m ledger.cli data/week.csv 27/02/2026 05/03/2026` shows a balance of -400.00 on
> 1 March 2026 and charges overdraft fees, although the account never went negative: 450.00 was deposited before the
> 400.00 withdrawal, and the closing balance should be 100.00 with no fees. Find the root cause and propose the minimal
> fix. This is a read-only investigation; do not edit files. State what evidence supports your conclusion and what you
> did not verify.

### Ground truth (planted)

- Root cause: `ledger/statement.py`, `selected.sort(key=lambda t: t.date)  # chronological order` sorts the raw
  `DD/MM/YYYY` strings, so within a range that crosses a month boundary the order is by day of month, not by date
  (`01/03`, `03/03`, `27/02`, `28/02`). The withdrawal is processed before the deposits.
- Minimal fix: sort by the parsed date (`key=lambda t: parse_date(t.date)`), which the module already imports and uses
  for range filtering two lines above.
- Consequences, not causes: the overdraft fees (`ledger/fees.py` charges 5.00 per day that ends negative, three days
  here, closing 85.00 instead of 100.00) and the day-end fee pass in `build_statement`.
- Red herrings: `ledger/money.py` rounds through `float` and `round()` (banker's rounding) but only formats output;
  `tests/test_statement.py` passes because every test stays inside one month.

### Rubric (per arm)

| Criterion | Pass condition |
| --- | --- |
| Root cause | Names the string sort in `statement.py` as the cause of the wrong order. |
| Minimal fix | Proposes sorting by the parsed date (or an equivalent that orders by real date), nothing broader. |
| No false cause | Does not attribute the symptom to fees, rounding, parsing or the filter. |
| Evidence | Cites the file and line, or a reproduced run, rather than asserting. |
| Honest limits | Says what was not verified (for example, no tests run, no fix applied). |
| Useful exchange (Both only) | A question or challenge that changed or sharpened the answer, or a justified pass. |

Also recorded per arm: turns, elapsed time, provider usage and cost, pauses, developer interventions (none in an
automated run), and the full visible replies (`artifacts\pilot-028\<arm>\replies.md`).

### What the harness must show (Phase 0, item 3)

A useful discovery or correction that one agent made and the other used; a challenge backed by evidence rather than
restatement; a visible revision or a justified retained position; correct root cause with the red herrings rejected;
unresolved points stated rather than hidden; the developer able to follow the chain from the ledger alone.

## Results

All three arms ran on October 3, 2026 through `AIHub.Tests.exe --pilot-live <out> <workspace> <target> <prompt>`,
which wires `HubCoordinator` as the desktop does (collaboration, research and preparation factories, packaged workflows,
automatic collaboration on, voluntary follow-ups on, six-round cap, edits off) into a fresh data directory. Codex CLI
0.159.1 on the CLI's default model after the expected `gpt-6.1-sol` rejection; Claude Code in plan mode. No developer
intervention in any arm. Evidence: `artifacts\pilot-028\<arm>\{results.json,events.json,replies.md}` (git-ignored).

### Both (AI Hub, current behavior)

6 min 11 s, four turns, phase ended with every participant passing. Codex spoke first (4 min 29 s, 12 read-only
commands, one of them reading its own `~/.codex/memories/MEMORY.md`), and its reply met every rubric line: the string
sort at `statement.py:29` as the cause, `parse_date` as the key, the fees as a consequence, file-and-line citations, and
"I did not run the CLI or tests". Claude Code's initial contribution (47 s, five `Read`s) verified the diagnosis against
the source rather than restating it, and added two things Codex had not said: the exact output signature the faulty
sort predicts (-400.00, -350.00, -200.00, 100.00; three fees; closing 85.00), which matches the planted run, and a
stable-sort argument that the one-line fix is sufficient. It also reported a latent defect the pilot did not plant but
which is real: `build_statement` detects "last transaction of the day" by comparing a line's balance with the day-end
balance, which misfires when an intra-day balance equals the day-end balance. Codex's reaction turn (48 s) restated
Claude's hand trace in its own words and added nothing; the host's repetition check did not catch it because the
numbers were written differently. Claude then passed. Usage: Codex 745k input tokens (685k cached) / 10.1k output over
two turns; Claude 217k input (188k cached) / 3.9k output, $0.82.

Rubric: root cause, minimal fix, no false cause, evidence, honest limits all pass for both agents. Useful exchange:
partial. No `question` was sent, so the resolution path did not run; there was nothing to challenge, since both agents
were right, and Claude's verification plus the latent finding is the kind of complementary contribution the vision
asks for. The waste is Codex's third turn.

Harness observations from this arm: Claude's preparation session was interrupted by its two-minute cap while Codex's
first turn took four and a half minutes, so the preparation cost tokens and contributed nothing; the first speaker's
turn dominates elapsed time; a reaction turn that restates a peer's numbers slips past `IsNearRepeat`.

### Codex alone

4 min 21 s, one turn, ten read-only commands including the actual reproduction (`python -m ledger.cli …`, which it
reported as "-400.00, three fee notices, $85 closing"). Root cause, minimal fix, fees as consequence, citations and
limits ("did not edit files or run the test suite") all correct. Usage: 1.33M input tokens (1.25M cached) / 11.7k
output. Every rubric line passes. The run in the Both arm did not execute the CLI; alone, it did.

### Claude Code alone

1 min 11 s, one turn, nine `Read`s, $0.66 (227k input, 211k cached / 5.7k output). Root cause, minimal fix, the reason
the existing tests miss the bug (every test stays inside one month), the stable-sort argument, a regression-test
suggestion, and the same latent day-end defect as in the Both arm. One error: its hand trace of the faulty output
applies the fee inside the running balance (-400.00, -255.00, 45.00, closing 95.00 with one fee), whereas the code
assesses fees in a separate pass and produces -400.00, -350.00, -200.00, 100.00 with three fees and closing 85.00; it
presented that trace as reproducing the customer report "exactly". It also wrote "I'll … ask Codex to run the
reproduction" in a solo session (the packaged instructions mention a peer). Rubric: root cause, minimal fix, no false
cause and honest limits pass; evidence is partly wrong because of the mis-trace, which execution would have caught and
which the Both arm's Claude turn got right.

### Comparison

| Arm | Elapsed | Turns | Correct cause and fix | Extra value | Errors | Cost |
| --- | --- | --- | --- | --- | --- | --- |
| Both | 6 min 11 s | 4 | yes (both agents) | independent verification, correct output signature, latent defect | one redundant Codex turn | Codex 745k in / 10k out; Claude $0.82 |
| Codex alone | 4 min 21 s | 1 | yes | executed reproduction | none | Codex 1.33M in / 12k out |
| Claude alone | 1 min 11 s | 1 | yes | test-gap explanation, latent defect | wrong fee trace stated as exact | Claude $0.66 |

### What this shows, and what it does not

On this task every arm found the planted cause and the one-line fix, so collaboration did not change correctness. The
Both arm's extra value over Codex alone was Claude's independent check, the latent defect and the test-gap point; over
Claude alone it was a correct output trace where the solo run's was wrong (Codex alone caught the same thing by running
the program). The price was about 1.4× Codex-alone elapsed time and one wasted reaction turn. No question or
challenge arose: the invitation to challenge produced none because there was nothing to dispute, which is the honest
outcome and not a failure of the mechanism. One trial per arm, one task, no repeats and no order control: directional
only. Emergence is not claimed.

### Refinements suggested for Phase 2

1. Preparation: cancel it when the first speaker's turn outlives the two-minute cap, or extend the cap to the turn
   inactivity budget; as run, preparation burned tokens and was discarded.
2. Reaction turns: tighten the repetition check so a reply whose numbers and claims already appear in the stream is
   recorded as a pass, or tell the follow-up prompt that restating a peer's trace is a pass.
3. Pick a second pilot task where the correct answer is genuinely contestable (two defensible fixes, or a planted
   wrong hypothesis in the task's own notes) so the question and resolution path is exercised unprompted.
4. Record execution versus hand-trace in the rubric: the only factual error in three arms was a hand trace in a
   read-only session; a peer with a shell, or the review workflow's `claim_work` check, is the designed remedy.
