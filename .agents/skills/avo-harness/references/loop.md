# The loop in detail

Read this when the task is dense, when you're improvising the iteration format, or when you don't know which variant
to pick.

## Why the loop has this shape

In AVO the same loop optimized GPU attention kernels for seven days and solved ARC-AGI-3 with no instructions and no
stated goal. The feedback channel changes: compiler and profiler in one case, environment transitions in the other.
The cycle doesn't:

1. form hypotheses on incomplete evidence;
2. act through an external interface;
3. observe the consequence;
4. keep what's useful;
5. revise the model of the problem;
6. recover from wrong assumptions;
7. carry on.

What transfers across domains isn't knowledge: it's the machinery.

## Variation operators

Every iteration you generate candidates. The catalog, from cheapest to most expensive:

| Operator | When | Example |
| --- | --- | --- |
| **Instrument** | You don't understand the state | Add a log or assert that discriminates between two hypotheses |
| **Bisect** | You know it used to work | `git bisect`, or halve the input |
| **Minimize** | The case is large and noisy | Reduce it to the minimal failing reproduction |
| **Isolate** | You suspect a component | Stub or mock the neighbor; see if the red persists |
| **Repair** | You have a diagnosis | The surgical change |
| **Rewrite** | Three repairs failed in the same place | Replace the whole block |
| **Question** | Everything above failed | What if the test is wrong? What if the goal is badly framed? |

The first four **buy information**. The last three **spend** it. When you're lost, buy; when you have a diagnosis,
spend. The classic mistake is repairing early: it's expensive, and it teaches nothing when it fails.

## How to choose between variants

Rank by *expected information / cost*, not by probability of success.

A variant with a 20 % chance of solving the problem, which runs in 5 seconds and rules out half a hypothesis, beats
one with 60 % that takes 10 minutes and, if it fails, tells you nothing. Ask yourself: **if this fails, what do I
learn?** If the answer is "nothing", pick something else.

## Budget and abandonment

Declare a budget per direction when you open it: N iterations, or T minutes. When it runs out, the direction
closes. You log it as discarded with what it taught, and you move to the next one. The budget isn't extended
"because it was about to work". That phrase is the sound of sunk cost.

Hard rule: **the same error signature twice closes the direction**. There is no third time. If the same error came
back identical, your model of the problem is wrong, not your implementation. Editing further at that level is
spinning in place.

## Recovery

When you close a direction, don't pick the next one at the same level of abstraction. Go up one:

- The fixes failed → **instrument** until you see the real state.
- The instrumentation failed → **minimize** until it fits in your head.
- The minimization failed → **question the premise**: the oracle, the goal, or the belief that the bug is where
  you're looking.

In the journal this shows up as a short, honest entry:

```markdown
## It 11 · closing direction "session cache"
Budget spent (4/4 iterations). Discarded: the TTL isn't the cause —
the bug reproduces with the cache disabled [OBSERVED, it 09].
Leveling up: I stop fixing and instrument the whole auth path.
```

## Pending evidence

When verification takes longer than writing the next variant, don't wait. The action leaves its oracle running in
the background, and the loop moves on to the next hypothesis.

- The journal logs the variant as `pending: <command> (started HH:MM)`.
- A red result jumps the queue: it's handled before any new hypothesis.
- A hung oracle (one that timed out) counts as red.
- At most 3 pending variants stacked on top of each other. With 3, work on another, independent line.
- Compilation is never left pending: verify it before moving on.

## When to stop and ask

Stop and consult the user when:

- the overall budget ran out;
- the supervisor returned `STOP AND ASK`;
- you found that the stated goal is inconsistent with what the code can do;
- a variant requires a destructive or irreversible action.

When you stop, present state and options, not a vague request for permission. For example: "I've exhausted the
three concurrency hypotheses; the evidence points to the scheduler reordering. I can (A) instrument with tracing,
expensive but definitive, or (B) force ordering with a semaphore, cheap but it hides the symptom. Which one?"
