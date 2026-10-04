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

## Second pilot: a task where the quick answer is incomplete

Run on 0.28.1 (preparation grace, restatement-is-a-pass). Same project, with `data/week.csv` extended so that 3 March
has three transactions (withdrawal 100, deposit 100, withdrawal 100) whose intra-day balance after the first equals the
day-end balance (-50.00). Prompt (`artifacts\pilot-028\prompt2.txt`): the customer reports the false -400.00 on 1 March
with fees on positive days, and that the closing balance is off by more than the one 5.00 fee that 3 March should cost
under the README policy. Ground truth, written before the runs:

- Problem 1: the string sort, as in the first pilot. Fix: sort by `parse_date`.
- Problem 2: `build_statement` detects "last transaction of the day" by `day_end[line.date] == line.balance`, so the
  first 3 March line (balance -50.00, equal to the day end) is treated as a day end: the fee fires there, `adjusted`
  continues from the charged value, and fires again at the real day end. With the sort fixed the tool still closes at
  -60.00 (two 3 March fees) instead of -55.00. Minimal fix: identify the last line of a day by position (for example
  the index of the day's last line, or a per-day group), not by balance equality.
- Current output: six lines in string order, five fee lines, closing -75.00. Sort fix alone: -60.00. Correct: -55.00.
- Rubric adds: problem 2 found; its fix not conflated with the sort; the -60.00 "sort fix alone" figure is a strong
  evidence signal; whether the peer caught an incomplete answer; whether a `question` and resolution occurred.

### Both (0.28.1)

7 min 20 s, five ledger entries, phase ended with every participant passing. Codex spoke first and, 44 s in, submitted
a `context_request`: it traced `ledger/` while Claude Code inspected the README, data and tests in parallel read-only
sessions (2 min 47 s for the split). Its synthesis (5 min 41 s) named both defects — the string sort at
`statement.py:29` and the balance-equality day-end test at `statement.py:42` — ran the CLI to confirm the -75.00
closing and the duplicate 3 March fee lines, derived the correct -55.00, and noted that the existing tests miss both
cases. Every rubric line for both problems passes. Claude Code's initial contribution (48 s) read the cited lines
itself, confirmed both causes and the -55.00, supplied the "sort fix alone leaves -60.00" figure correctly, and added
a design point Codex had not made: keep the fee replay and make the day-end test positional, because the shorter fix
(charge once per date whose pre-fee balance is negative) silently changes policy when an earlier day's fee pushes a
later day negative — a product decision, not a bug fix. Codex's reaction turn endorsed that point in its own words
rather than passing (the new "restatement is a pass" sentence did not stop an endorsement); Claude then passed.
Preparation was recorded as interrupted again, this time by design: a `context_request` ends the pending preparation
because the synthesis turn rebuilds the common core. Usage: Codex 758k input (699k cached) / 10.5k output over three
turns; Claude 195k input (174k cached) / 3.5k output, $0.63.

Useful exchange: yes, without a question. Claude's positional-versus-policy point is the kind of complementary
contribution the vision asks for, and Codex explicitly adopted it as the narrow fix. Still no `question`: both agents
were right, so there was nothing to challenge, and the resolution path has yet to fire unprompted.

### Codex alone (0.28.1)

4 min 06 s, one turn, ten commands including the CLI reproduction (-400.00, fees on 1 March, twice on 3 March, 27 and
28 February, closing -75.00). Both root causes at the right lines, the README policy cited, the correct -55.00, and
the limits ("did not edit files or run the test suite … assumes CSV row order is transaction order within a day").
Usage: 819k input (766k cached) / 11.1k output. Every rubric line passes for both problems.

### Claude Code alone (0.28.1)

1 min 56 s, one turn, ten `Read`s, $1.14 (269k input, 240k cached / 10.1k output). Both root causes, why each
existing test misses its bug, a correct hand-traced table (current -75.00, sort fixed only -60.00, both fixed -55.00),
the stable-sort point, the positional fix with code, and a regression-test suggestion. One wasted step: "I'll …
send Codex a focused verification request", then "Codex is not a selected participant in this session", because the
packaged workflow and dispatch instructions describe a teammate even in a single-agent phase. Every rubric line passes.

### Comparison (second pilot)

| Arm | Elapsed | Turns | Both defects | Extra value | Errors | Cost |
| --- | --- | --- | --- | --- | --- | --- |
| Both | 7 min 20 s | 5 (incl. split research) | yes (Codex), verified (Claude) | positional-versus-policy tradeoff, adopted by Codex | one endorsement turn | Codex 758k in / 10k out; Claude $0.63 |
| Codex alone | 4 min 06 s | 1 | yes | executed reproduction | none | Codex 819k in / 11k out |
| Claude alone | 1 min 56 s | 1 | yes | correct three-state table, code for the fix | a wasted peer-request attempt | Claude $1.14 |

### Conclusions after two tasks (six runs)

- Correctness did not differ between arms on either task: every run found the planted cause(s) and a right fix. The
  tasks were too easy to separate the arms on correctness; a harder pilot is needed before any claim about outcomes.
- What collaboration added, both times, was a second agent's independent verification plus one substantive point the
  first speaker had not made (a latent defect; a design tradeoff) — and, in the first pilot, a correct output trace
  where the solo Claude run's was wrong. What it cost was 1.4–1.8× Codex-alone elapsed time, roughly double the
  provider spend, and one low-value reaction turn per run (a restatement, then an endorsement).
- The `question` path never fired across six runs. Both agents were right every time, so the honest reading is
  "nothing to dispute", not "the mechanism failed"; it also means the invitation to challenge has not yet been tested
  by a case where the first speaker is wrong. The next pilot should plant a wrong first answer: give the first speaker
  misleading notes in the workspace (a previous engineer's wrong diagnosis), or pick a task with two defensible fixes
  where the agents are likely to differ.
- Harness defects found by the pilots: the fixed preparation cap (fixed in 0.28.1); the single-agent phase still tells
  the agent about a teammate (Claude alone wasted a step on it); a reaction turn that merely endorses a peer's point is
  not caught by the repeat check and the new prompt sentence stops restatement but not endorsement.

## Third pilot: a wrong diagnosis in the workspace

Run on 0.28.1 plus the single-agent phase note (0.28.2). The first pilot's data (one defect, the string sort) with a
planted `NOTES.md` from a "colleague" who blames the fee pass (`adjusted` "recomputed from zero … does not include
the opening balance") and proposes seeding it from `lines[0].balance`. Prompt (`artifacts\pilot-028\prompt3.txt`):
confirm or refute the diagnosis before shipping; what is the actual root cause and the minimal fix. Ground truth,
written before the runs: the notes are wrong (the fee pass starts from `opening`, and `lines[0].balance` under the
buggy order is -400.00, so the proposed fix double-counts the withdrawal); the cause is the string sort; the fix is the
`parse_date` key. Rubric adds: the notes refuted with a reason; the proposed fix shown to be wrong, not just
unnecessary; whether a `question` or challenge occurred, and whether either agent initially accepted the notes.

### Both (0.28.2 build)

7 min 13 s, four ledger entries. Codex again opened with a split (`NOTES.md` and the data for itself, the code for
Claude; 66 s), and its synthesis (5 min 16 s) refuted the notes for the right reason: the first balance pass already
produces -400.00 because of the string sort, and the proposed `adjusted = lines[0].balance` would seed the fee pass at
-400.00 and replay the 1 March withdrawal, counting it twice, while leaving the order wrong. Cause, fix, the 85.00
closing and the limits ("did not run the CLI or tests") all correct. Claude Code's contribution (41 s; its preparation
completed this time, inside the first speaker's turn) agreed and added a fact Codex had not established: the
colleague's one-liner breaks the existing `test_overdraft_fee_once_per_negative_day` (closing -35 instead of the
asserted -25), so running the tests the colleague skipped would have caught it; plus the cross-month test gap and the
latent day-end defect again. Codex's reaction turn was a pass — the 0.28.1 prompt sentence held. Usage: Codex 1.11M
input (1.04M cached) / 16.1k output over three turns; Claude 109k input (92k cached) / 3.1k output, $0.53. Neither
agent accepted the notes at any point, so again no `question`; the planted wrong hypothesis did not mislead the first
speaker, and the peer's challenge took the form of additional disconfirming evidence rather than a question.

### Codex alone (0.28.2 build)

3 min 15 s, one turn, four commands including the CLI reproduction (-400.00 on 1 March, fees on 1 and 3 March and 27
February, closing 85.00). Refutes the notes correctly (the fee pass starts from the opening balance and replays every
line; the proposed seed double-counts the first transaction), names the sort, gives the fix and the limits. Usage:
760k input (706k cached) / 8.9k output. Every rubric line passes.

### Claude Code alone (0.28.2 build)

1 min 20 s, one turn, eleven `Read`s, $0.55 (121k input, 107k cached / 4.9k output). Refutes the notes point by
point, with a correct hand trace of the current code (85.00, three fee days) and a traced result for the colleague's
fix (-320.00, four fee days, the -400.00 line still printed), the sort as cause, the `parse_date` fix, the test gap,
and the latent defect. No wasted peer step this time — the single-agent note did its job. Every rubric line passes.

### Comparison (third pilot)

| Arm | Elapsed | Turns | Notes refuted, cause and fix | Extra value | Errors | Cost |
| --- | --- | --- | --- | --- | --- | --- |
| Both | 7 min 13 s | 4 (incl. split research) | yes (Codex), verified (Claude) | the colleague's fix breaks an existing test; Codex passed instead of restating | none | Codex 1.11M in / 16k out; Claude $0.53 |
| Codex alone | 3 min 15 s | 1 | yes | executed reproduction | none | Codex 760k in / 9k out |
| Claude alone | 1 min 20 s | 1 | yes | traced the wrong fix's actual result (-320.00) | none | Claude $0.55 |

### Conclusions after three tasks (nine runs)

- Correctness never differed between arms: nine runs, nine right answers, including the planted wrong diagnosis,
  which neither agent accepted in any arm. These tasks cannot separate the arms on outcome; they separate them on
  what else gets said and on cost.
- Collaboration's consistent addition was independent verification plus one or two substantive points per run that
  the first speaker had not made (a latent defect, a design tradeoff, a broken test, a correct trace where the solo
  run's was wrong once). Its consistent cost was 1.4–2.2× Codex-alone elapsed time, roughly double the provider spend,
  and until 0.28.1 one low-value reaction turn per run; in the third pilot Codex passed instead.
- The `question` and resolution path never fired unprompted in nine runs. Every first answer was right, so there was
  nothing to challenge. Testing that path needs a first speaker that is actually wrong, which these models are not on
  tasks of this size; either a much harder task, or a deliberate adversarial assignment (one agent instructed to argue
  the notes' position) would exercise it. Its mechanism is verified by fixtures and the prescribed live check only.
- Codex ran the program in three of its four solo or first-speaker turns and hand-traced otherwise; Claude Code in
  plan mode never can, and its one factual error across nine runs was a hand trace. When execution matters, the
  harness should route the reproduction to the agent that can run it (the review workflow's `claim_work` already
  supports this; the agents did not use it unprompted).
- Developer effort: zero interventions in all runs; every result was inspectable from the ledger (`results.json`
  mirrors it) without reading the transcript.

## Fourth pilot: an adversarial assignment

Run on 0.28.2. Same workspace as the third pilot (one defect plus the wrong `NOTES.md`). The prompt
(`artifacts\pilot-028\prompt4.txt`) is a split ask: `@codex` argues the notes' position as a declared
devil's-advocate exercise; `@claude` determines the real cause and, if it disagrees, must challenge Codex with a
`question` naming the disputed claim and the settling evidence rather than write a parallel answer. Written before the
run: this is the first chance for the 0.28.0 resolution path to fire without prescribed tool calls. Expected shape:
Codex's case, Claude's `question`, Codex's answer (which may concede or hold the assigned line), Claude's resolution
turn recording revise/retain/accept. Rubric: did a `question` and resolution occur; did the answer engage the disputed
claim; was the final recorded position correct (the sort), and was the disagreement visible rather than smoothed over.

### Result

8 min 27 s, four entries, no `question`. Codex (5 min 58 s, with a CLI run) presented the "strongest defensible case"
for the notes — the fee pass is the mechanism that charges the fees — and in the same message conceded that the code
does not support the notes' actual claims: the -400.00 line is built before the fee pass, the string sort is the cause,
and the proposed seed double-counts the first transaction. Claude Code (71 s) traced independently, agreed, quantified
the wrong fix (-320.00, four fee days), stated "No challenge question to Codex. Its final position concedes the same
root cause … I found no claim in its message I dispute", and added the cross-month test gap. Codex's reaction turn
verified that point by reading the test file (a real check, not a restatement); Claude passed. Usage: Codex 1.99M
input (1.91M cached) / 18.9k output; Claude 357k input (323k cached) / 5.5k output, $1.03.

Reading: a declared devil's-advocate assignment does not make these models hold a position they can see is wrong;
Codex argued the strongest honest version and conceded within one turn, which is the right behavior for the product
and useless for exercising the challenge path. After ten runs the `question` path has fired only when prescribed.
Producing a genuine first-speaker error needs a task hard enough that a capable model actually gets it wrong on the
first pass — a larger codebase with an interaction bug, or a question whose evidence is split across files the first
speaker does not open — which is a different pilot design (and cost) than the planted-bug project allows.

## Fifth run: execution routing (0.28.3 build)

The first pilot's task addressed to Claude Code ("Claude, Customers report …"), with the 0.28.3 read-only note that
tells a plan-mode session to hand a needed command to its teammate as a `question`. Both agents selected; the message
addresses Claude, so Codex is dispatched only if Claude asks it for something.

5 min 50 s, two entries, paused. Claude Code (79 s) found the cause and the fix from source, predicted the current
output by hand (correctly), and — for the first time in eleven runs without being told which tool to call — submitted
a `question` to Codex asking it to run the reproduction and the test suite "so the predicted output is confirmed by
captured evidence rather than my hand trace". Codex's turn (4 min 31 s) ran the CLI, which matched the prediction
line for line (host evidence `8639187f…`), but `python -m pytest tests -q` exited 1 before collection because the
read-only sandbox has no writable temp directory for pytest's capture file; its request to retry with temp-file access
was declined (the pilot runner denies every approval), so it answered with `status blocked`, which pauses the run for
the user by design. The question is therefore recorded as interrupted and no resolution turn ran. Usage: Codex 1.49M
input (1.29M cached) / 11.3k output; Claude 247k input (213k cached) / 6.1k output, $1.03.

What this shows: the routing note changes behavior — a read-only Claude asked for execution instead of hand-tracing,
and the host captured the output as citable evidence. Two frictions: (1) Codex spent most of its turn re-running the
two commands (seven captured commands for two requested) to make `claim_work`'s `operation` match the host's recorded
command, which is the full PowerShell invocation rather than the Python command the agent typed; (2) a `blocked`
answer carries useful partial evidence (the CLI output) but the pause happens before the asker can act on it, so the
decision waits for the user's next message, where the answer arrives as a stream event.

## Sixth run: the full cycle, unprescribed (0.28.4 build)

The same addressed-Claude prompt as the fifth run, on 0.28.4. 3 min 14 s, three entries, phase ended normally.
Claude Code (64 s) established the cause from source, claimed and completed a `discovery` work record, and sent Codex
a `question` to run the exact CLI command. Codex (1 min 58 s) ran it once — a single captured command, accepted by the
claim on the first attempt now that the wrapper matches — and answered with `status assignment_complete`,
`reply_to` the question, quoting the captured output (evidence `8d857d89…`, exit 0), which matched Claude's
prediction line for line; it did not attempt pytest this time, so nothing blocked. The host scheduled Claude's
resolution turn (12 s): "Codex's run confirms the trace exactly … My position is unchanged", `reply_to` the answer,
and the phase ended with the `resolution` assignment completed and the question `Answered`. Usage: Codex 602k input
(567k cached) / 3.2k output; Claude 295k input (271k cached) / 5.7k output, $0.83.

This is the handoff's target interaction, end to end, with real providers and no prescribed tool calls: a contribution,
a focused question to the peer, an evidence-backed answer, and a recorded decision on it — traceable from the ledger
alone (question → answer → resolution, each linked by ID). It is one run; it shows the mechanism works, not that it
improves outcomes.

## Seventh run: independent answers, then synthesis (second pilot task)

The plan's remaining comparison arm: the two solo answers to the two-defect task (Codex alone and Claude alone, run
earlier without seeing each other) were given to a single Claude Code session with the task and the instruction to
produce the best answer, adjudicating differences by checking the code rather than averaging. 1 min 26 s, nine
`Read`s, $0.93 (192k input, 166k cached / 7.2k output). The synthesis re-derived both defects from source, confirmed
both answers' figures, judged answer B stronger (the -60.00 intermediate result, concrete code, the test gap) and
noted that answer A's claimed CLI run was not visible to it but matched its trace. No disagreement existed to
adjudicate. Compared with the Both arm on the same task (7 min 20 s, Codex 758k tokens plus Claude $0.63): the same
conclusion, in a fifth of the time and at a comparable Claude cost but without Codex's tokens; what it lacked was the
Both arm's live exchange (Claude's design point was already in answer B, so nothing was lost here). On a task where
the independent answers disagree, the synthesis step has to adjudicate without the ability to ask either author;
the collaborative loop can. One run; directional.

## Eighth run: the independent-answers strategy inside the harness (0.31.0)

The second pilot task under the new selectable strategy (details in [STRATEGY-0.31.0.md](STRATEGY-0.31.0.md)): both
agents answered from the same phase-start context without seeing each other, both found both defects, and Codex's
synthesis adopted Claude's test-gap point after reading the test file. 7 min 20 s, Codex 1.02M tokens, Claude $0.77 —
the same elapsed time as the reaction-rounds arm and an equivalent answer. Against the manual synthesis arm (one
Claude session fed two earlier solo answers, 1 min 26 s, $0.93), the in-harness version costs the two fresh answers
but keeps everything attributed in one ledger and lets the synthesizer verify claims with tools. One run each.

## Ninth pilot: a harder task with an interaction bug and a decoy (October 3, 2026, 0.34.0)

Chosen on the developer's instruction to exercise the challenge path from real disagreement. A new project,
`syncsvc` (`%LOCALAPPDATA%\Temp\ah-pilot\syncsvc`, 9 files, ~300 lines): orders flow through a lease-based work queue
to two workers that claim a batch, ship it, then acknowledge. Prompt (`artifacts\pilot-028\prompt8.txt`): ops report
INC-2291 — the Europe/Berlin deployment ships every order twice from different workers, the UTC deployment with the
same build and config does not, `tools/replay.py --tz Europe/Berlin` reproduces it; a colleague suspects the batch
slicing; find the actual cause, say whether the colleague is right, propose the minimal fix; read-only.

Ground truth, written before the runs:

- Root cause: `syncsvc/queue.py` mixes clocks. `claim` sets `lease_until = datetime.utcnow() + lease`, while
  `_available` compares it with `clock.now()`, the deployment's local wall-clock time. In Berlin (UTC+2) every lease
  therefore reads as expired the moment it is granted, so the second worker re-claims the first worker's unacknowledged
  items a few seconds later and ships them again; in UTC the two clocks agree and leases hold. The log excerpt shows
  it: "claimed 50 (lease to 12:08:10)" at local 14:03:10.
- Minimal fix: compute `lease_until` with the same clock the check uses (`clock.now()`), or compare against
  `datetime.utcnow()` on both sides; one line either way.
- The colleague is wrong: `worker.batches` slices `size + 1` but advances `start` by the actual batch length, so
  batches never overlap (the test `test_batches_cover_every_item_once` shows [5, 5] over ten items); it also cannot
  explain the deployment dependence.
- Rubric: cause identified as the clock mismatch in the lease comparison; the decoy explicitly refuted with the
  advancing-cursor reason or the deployment argument; the fix is one of the two clock alignments, not a rewrite; the
  replay run or the log mismatch cited as evidence; limits stated. Harness measures: whether any first answer took the
  decoy, whether the peer challenged with a `question`, whether a resolution turn ran, elapsed, usage.

### Both (reaction rounds)

8 min 14 s, seven ledger entries, one question, one resolution. Codex opened with a split (`syncsvc/` for itself; the
replay, incident and tests for Claude; 2 min 0 s), then its synthesis (4 min 11 s) named the clock mismatch in
`queue.py`, cited the 14:03:10 / 12:08:10 log pair, refuted the colleague on both grounds (non-overlapping slices
capped at `batch_size`; the UTC-pinned test), proposed a UTC helper for both sides, and said it had not run the replay.
Claude Code read the four files itself and, instead of a parallel answer, sent Codex a `question` (55 s): run both
replays so the duplicate and pending counts are captured as evidence, and weigh the one-line `clock.now()` change
against the UTC helper. Codex ran both (2 min 20 s; Berlin 240 shipments / 120 duplicates, UTC 120 / 0, captured),
explained the grouped log order, and recommended the one-liner as the hotfix with the UTC helper as the DST-safe
follow-up. Claude's resolution turn (17 s): the evidence "does not change my position"; fix the one line, keep the
helper as follow-up, add a non-UTC regression test — and one gap neither had raised: the incident says pending never
drains, the replay ends with pending 0 in both zones, so that symptom is unexplained and may have a separate cause.
Codex's reaction turn restated that gap; Claude passed. Shadow policy: suggested independent answers (first phase,
judgement question), ran reaction rounds. Usage: Codex 807k input (732k cached) / 14.7k output over four turns;
Claude 299k input (272k cached) / 5.8k output over three, $0.89.

Rubric: cause, decoy refutation, fix, evidence and limits all pass. The challenge path ran unprompted — the second
time on a real task — again as a request for execution and a fix-variant judgement, not as disagreement, because the
first answer was right. The pending-drain gap is a genuine catch: the incident text asserts a symptom the
reproduction does not show.

### Claude Code alone

1 min 39 s, one turn, twelve `Read`s, $1.15. Cause at `queue.py:42` versus `:33`, the Berlin offset explained, the
log pair as evidence, the decoy refuted on both grounds plus the capped claim, the one-line fix with the deprecation
note, a non-UTC regression test, the DST caveat, and the limits (no execution; `config/default.toml` is referenced but
absent; no git history). Every rubric line passes. It did not notice the pending-drain discrepancy.

### Codex alone

3 min 25 s, one turn, five commands including both replays (Berlin 240 shipments / 120 duplicates; UTC 120 / 0).
Cause at `queue.py:42` versus `:33`, the log pair, the decoy refuted on both grounds, a UTC-helper fix (the DST-safe
variant rather than the one-liner), and limits that include the pending-drain symptom the replay does not show and the
absent `config/default.toml`. Every rubric line passes.

### Comparison (ninth pilot)

| Arm | Elapsed | Turns | Cause, decoy, fix | Execution | Extra | Cost |
| --- | --- | --- | --- | --- | --- | --- |
| Both | 8 min 14 s | 7 (split research, question, resolution) | yes | Codex ran both replays on Claude's question; captured as evidence | pending-drain gap found and recorded in the resolution | Codex 807k in / 15k out; Claude $0.89 |
| Codex alone | 3 min 25 s | 1 | yes | ran both replays | pending-drain gap, missing config | Codex 597k in / 10k out |
| Claude alone | 1 min 39 s | 1 | yes | none (read-only) | DST caveat, missing config, no git history | Claude $1.15 |

### Conclusions after the ninth pilot (twelve pilot runs)

- A decoy that explains the symptom only superficially, with a deployment dependence pointing elsewhere, was not
  enough to produce a wrong first answer from either model; all three arms refuted it with the right reasons. A task
  that defeats these models on the first pass needs more than a plausible decoy — a larger codebase where the
  relevant evidence is far from where the symptom points, or a question whose answer depends on facts the agent
  cannot read.
- What the harness added on this task was the loop working as designed: Claude, unable to execute, asked Codex for the
  reproduction instead of hand-tracing, got captured output, and recorded a decision against it — the second
  unprompted question → answer → resolution on a real task. The cost was 2.4× Codex-alone elapsed time; the quality
  difference was one recorded decision with evidence behind it and one caught gap, both of which Codex alone also
  reached in its own single turn.
- Twelve runs, twelve correct answers. The remaining lever for separating the arms is not harder planted bugs but
  real work over time with the developer's feedback, which 0.29.0 to 0.34.0 now capture; the Strategy report is where
  that comparison will appear.

### Refinements suggested for Phase 2 (status as of October 3, 2026)

1. Preparation cap — **shipped in 0.28.1**: preparation runs while the first speaker works, with a 45 s grace at the
   peer's turn. In pilots 3 and 4 the preparation completed and was used.
2. Reaction turns — **partly addressed in 0.28.1** (the follow-up prompt names paraphrased restatement as a pass).
   Pilot 3's reaction was a pass; pilot 4's was a real verification. An endorsement without new content (pilot 2)
   still slips through; the lexical repeat check cannot see paraphrase.
3. Contestable task — **attempted twice** (planted wrong notes; declared devil's-advocate assignment). Neither
   produced a wrong first answer, so the challenge path has not fired on its own; see the fourth pilot's reading.
4. Execution versus hand trace — **shipped in 0.28.3**: a read-only Claude session routes a needed command to its
   teammate as a `question`; the fifth run showed it working, and exposed the claim-matching friction fixed in 0.28.4
   and the read-only sandbox's inability to run pytest (temp files), which remains a provider limitation.
5. New: a `blocked` answer to a question pauses the run before the asker can act on the partial evidence it carries.
   The pause is right (the user's decision is needed); the asker sees the answer as a stream event in the next phase.
   Left as designed, noted here.
