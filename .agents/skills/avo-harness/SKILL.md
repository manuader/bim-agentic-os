---
name: avo-harness
description: >-
  Long-horizon agent harness in the style of NVIDIA's AVO: a hypothesis → variants → action → executed evidence →
  commit/revert loop, persistent memory in a journal that outlives the context, and a supervisor that detects
  stalling and forces a pivot. Use for any work that won't be solved in one pass: debugging after a failed attempt,
  flaky tests, performance optimization, large refactors, migrations, turning a red CI green, "it's broken and I
  don't know why", "make it work", integrating against a real API or hardware, tasks of more than ~5 iterations or
  that span sessions, and any resumption of earlier work. Also when the user says "AVO mode", "long horizon", "don't
  get lost", "keep going until it works". When in doubt, open it: its gate dismisses it in seconds if it doesn't fit.
  Don't open it for questions, reading or explaining code, edits with an obvious result, or work whose "done" is
  judged by a person rather than a command (UI, copy, docs, naming, unimplemented architecture decisions).
---

# AVO harness — long-horizon autonomous work

## Gate — before anything else

You are here because the description matched the request, not because anyone verified the harness is needed.
Verify it yourself, now, in ten seconds. The description is deliberately generous: opening it needlessly costs a
few thousand tokens, while not opening it when needed costs the whole task. That is why the fine filter lives here
and not there.

Close it and carry on without the harness if the request is:

- a question, a read, or an explanation of code;
- an edit whose result you already know before running anything;
- work whose "done" is judged by a person and not by a command: UI, copy, docs, naming, API names;
- a design or architecture decision not yet implemented;
- something you estimate at under ~15 minutes of work.

Leave silently: do the task the normal way, without announcing that you dismissed the harness. Having consulted a
skill isn't useful information for the user, and saying so turns a non-event into noise.

Stay only if **both** hold:

1. **There is an oracle, or one can be built**: a command that exits 0 when the task is done. Without it the loop
   has nothing to land on and becomes ceremony.
2. **One pass isn't enough**: an attempt already failed, or the task is long, or it spans sessions, or exploration
   is needed before deciding.

If (1) holds but not (2): do the direct pass first and come back here if it fails. Coming back is cheap. That
failure is better evidence about the task's real size than any upfront estimate.

Special case: the user explicitly asked for the harness ("AVO mode", "long horizon", "don't get lost"). Stay, even
if the task looks small. They asked for the discipline, not for your assessment of whether they need it.

## The thesis

A frontier model is *a component* of the agent, not the agent. What decides whether a long task ends well is the
system around it:

- what context the model receives;
- what state survives between invocations;
- how feedback lands;
- how it recovers when it was wrong.

NVIDIA made the point with AVO. The same model that scores ~30 % on ARC-AGI-3 alone scores 100 % inside the
harness, with ~12 % fewer actions than the comparable system. The difference wasn't the model. It was the machinery
of sustaining progress.

This skill is that machinery, in five pieces:

| Piece | What it does | Failure it prevents |
| --- | --- | --- |
| **Oracle** | An executable command decides whether something works | Declaring victory without evidence |
| **Journal** | Durable state outside the context | Re-exploring what was already ruled out |
| **Variation** | Several hypotheses per iteration; you pick the cheapest to falsify | Marrying the first idea |
| **Commit/revert** | Only what is verified stays | Piling up broken changes |
| **Supervisor** | An outside look that spots the sterile loop | Ten iterations spinning in place |

## Startup

Four things before touching code, in this order, because the order is the point.

**1. Define the oracle.** A command that exits 0 when the task is done and non-zero while it isn't:
`pytest tests/test_auth.py -x`, `cargo test --release`, `npm run build && node bench.js`,
`curl -sf localhost:8080/health`.

If no oracle exists, **building it is iteration 1**, before any fix. An agent without an oracle isn't solving the
problem; it is generating plausible text and hoping. This is the highest-leverage rule here: in AVO the feedback
came from compilers, profilers and environment transitions, never from the model's self-assessment.

If the user can't give you an oracle and one can't be built, say so explicitly and work with the uncertainty
flagged, not hidden.

**2. Create the run.**

```bash
mkdir -p .claude/avo/$(date +%F)-<slug>
grep -qxF '.claude/avo/' .git/info/exclude 2>/dev/null || echo '.claude/avo/' >> .git/info/exclude
```

If there's no git repo or the directory isn't writable, use the session's scratchpad and tell the user. The journal
goes in `journal.md` inside that directory. The template is in `assets/journal.md`.

**3. Write the charter**, the journal's header. It holds:

- the goal, in one sentence;
- the oracle;
- the budget: iterations or time before checking back with the user;
- the known constraints.

Without it, by iteration 12 you'll be optimizing something nobody asked for.

**4. Checkpoint.** Run `git stash list` and `git status` to know where you're starting from. If the tree is dirty,
ask before touching anything.

## The loop

Each iteration is a closed cycle. The full detail and the catalog of variation operators are in
`references/loop.md`. Read it if the task is dense, or if you catch yourself improvising the format.

1. **Orient.** Reread the journal's current state: not the whole context, the journal. What do I know *observed*?
   What am I *assuming*?
2. **Hypothesize.** Make one falsifiable claim about why the oracle is red. "The race is in the writer's flush",
   not "there's a concurrency problem".
3. **Vary.** Propose 2–3 distinct directions, not one. Choose by *information per unit of cost*: the one that most
   quickly tells you that you were wrong, not the one that most resembles the final solution. This is AVO's
   variation operator: the agent decides what to inspect, what to change and what to test, instead of following a
   fixed pipeline.
4. **Act.** Make the smallest change that discriminates between the variants.
5. **Land.** Run the oracle. Keep the real output, not your reading of it.
6. **Judge.** Does the evidence confirm or refute the hypothesis? A refuted hypothesis is progress: it narrowed the
   space. Log it as discarded.
7. **Commit or revert.** Green and no regressions: commit or checkpoint. Red: revert. Don't leave speculative changes
   piled up. In AVO there were 500+ directions explored and 40 versions committed, and that ratio is healthy.
8. **Record.** Write a journal entry before going on, while you still know.

If the oracle takes longer than writing the next variant, don't wait: leave it in the background and keep going.
The rules are in "Pending evidence" in `references/loop.md`.

### Entry format

```markdown
## It 07 · hypothesis: the race is in the writer's flush
Variants: (A) mutex on flush · (B) buffered channel · (C) synchronous fsync
Chosen: A — the cheapest to falsify (3 lines, runs in 8 s)
Action: mutex around Writer.flush — storage/writer.go:88
Oracle: `go test -race ./storage` → RED (same panic, 3/10 runs)
Evidence [OBSERVED]: the panic now shows up in Reader.next, not in flush
Verdict: REVERT. Discarded: "the race is in the writer".
Next: H2 — the iterator shares a buffer across goroutines
```

Mark each claim as `[OBSERVED]` (executed output you saw) or `[ASSUMED]` (inference). Silent assumption drift is
the most common way an agent gets lost after iteration 10: something you assumed in iteration 3 becomes a premise in
iteration 9 without ever being verified.

## Supervision

The supervisor is an **outside** look at the trajectory, not at the current step. You, inside the loop, are the
worst judge of whether the loop is working. That's why in AVO it is a separate component.

Trigger it when any of these happens:

- 3 iterations without the oracle changing state;
- the same error signature twice;
- two reverts in a row;
- an iteration that produced no new evidence;
- you are about to try something already marked as discarded;
- you've spent >40 % of the budget with <30 % of the goal.

How: launch the `avo-supervisor` subagent and pass it **only the journal path**. Its value lies in arriving without
your contaminated context. If subagents aren't available, do the pass yourself, reading the journal from scratch,
with the questions in `references/supervisor.md`.

It returns one of four verdicts: `CONTINUE` · `PIVOT` · `LEVEL UP` · `STOP AND ASK`. Write the verdict in the
journal and **follow it**. If you ignore it, the supervisor doesn't exist.

`references/supervisor.md` has the intervention playbook. It covers what to do concretely with each verdict, and the
recovery maneuvers: instrument instead of fixing, bisect instead of guessing, question the test instead of the code.

## Memory and resumption

The context is disposable; the journal is the state. Write for the Claude who will read this in another session
with nothing else.

When starting any work, look for open runs before starting from scratch:

```bash
ls -t .claude/avo/*/journal.md 2>/dev/null | head -5
```

If there is an open one:

1. read the charter, the current state and the discarded list;
2. run the oracle to see where you stand today;
3. only then decide the next step.

Resuming badly, by re-exploring what was already discarded, is exactly the waste the harness exists to prevent.

When the journal passes ~200 lines, compact it: the discarded list and the current state survive, and the
iteration-by-iteration narrative gets summarized. `references/memory.md` has the full schema and the compaction
rules.

## Closing

You're done when the oracle is green and a clean run confirms it. Report to the user:

- what is now working;
- what evidence backs it: the command and its output;
- which directions you discarded, and why;
- what was left undone.

The discarded list is the most valuable part and the one always omitted: it keeps the next session from repeating
your week.

If you stop without green, say it plainly, with the exact state and the best live hypothesis. A precisely reported
red is worth more than an invented green.

## Antipatterns

- **Deferred oracle**: "I'll fix first, then figure out how to test it". That's how ten changes pile up without
  knowing which one helped.
- **Ceremonial journal**: filling it with what you did instead of what you learned. If an entry doesn't narrow the
  search space, it's surplus.
- **Single variant**: proposing one direction lets the first intuition decide the rest of the task.
- **Decorative supervisor**: asking for the verdict and carrying on unchanged.
- **Output optimism**: reading a red log and counting it as progress because the message changed. The message
  changed, not the state.
