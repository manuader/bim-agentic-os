# Supervision

The supervisor exists because from inside the loop you can't see the loop. After several iterations the context is
full of your own reasoning, and each next step looks reasonable *given the previous one*. That's exactly how a
sterile line of work sustains itself. In AVO the supervisor watches the whole trajectory and redirects when the
search stalls, while the main agent keeps deciding what to inspect and what to test.

## How to invoke it

**Preferred: the `avo-supervisor` subagent.** Pass it **only the journal path**. Its whole value lies in arriving
without your context. If you tell it your theory, you've contaminated it and it will agree.

**Without subagents: do the pass yourself.** Reread the journal **from the charter**, as if someone else wrote it.
Answer the questions below in writing before drawing a conclusion.

## The questions

1. What is *observed* and what is *assumed*? Has an assumption become a premise without ever being verified?
2. Did the last three iterations narrow the search space, or only walk it?
3. Does an error signature, a file, or a hypothesis reworded in other words keep coming back?
4. Is the oracle still measuring what the goal asks for? Or has it drifted toward something easier to turn green?
5. Is there a cheap, obvious direction nobody tried because the loop closed early around the first theory?
6. Is the charter's goal still the goal?

## The verdicts

**CONTINUE**: there is new evidence each iteration, and the live hypothesis still stands. Log it anyway: later it
tells you when it stopped being true.

**PIVOT**: the current direction isn't paying off, but the overall diagnosis holds. Close the direction with what
it taught, and open the next one from the operator catalog. It's the most common intervention.

**LEVEL UP**: the problem isn't where you're looking for it. Stop editing and change operator:

- instrument instead of fixing;
- bisect instead of guessing;
- minimize instead of reading;
- question the test instead of the code.

Concretely: if three repairs failed in the same file, the file isn't the problem.

**STOP AND ASK**: the budget ran out, the goal turned out to be inconsistent, or a decision belongs to the user.
Present the state, the evidence, and two options with their cost.

## What to write in the journal

```markdown
## Supervision · after it 09
Verdict: LEVEL UP
Reason: three iterations editing auth/session.py, same error signature in
all three. The hypothesis "the token expires early" was never verified — it
has been marked [ASSUMED] since it 04 and used as a premise in 06, 07 and 08.
Action: instrument the token lifecycle before touching another line.
```

A verdict that doesn't change what you do next is worse than not asking for one. It costs time, and it leaves you
feeling you verified something.
