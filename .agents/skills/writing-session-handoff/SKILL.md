---
name: writing-session-handoff
description: Use when finishing a stretch of work, before switching to unrelated work or a fresh context, when handing off to another agent or a future self, when a session produced decisions or traps worth recording, or when asked to document what was just done
---

# Writing a Session Handoff

## Overview

Closing a session produces **three** outputs, not one:

| Output | Answers | Lifetime |
|---|---|---|
| `docs/SESSION-<date>-<topic>.md` | *why is it like this?* | permanent, never edited after |
| `handoffs/HANDOFF-<date>-<topic>.md` | *what do I do now?* | ephemeral, gitignored |
| new entry in `docs/HANDOFF.md` | *where does the project stand?* | permanent |

**REQUIRED BACKGROUND:** the model, the golden rule and the honesty requirement live in **handoff-system**. The third output is **writing-general-handoff**.

Skipping the third is the common failure: the session doc gets written, the general rots, and the next agent has to read everything.

Full templates for both documents: `references/templates.md`.

## Gather evidence before writing

Memory smooths over exactly the parts that cost time. Start from the record:

```bash
git log --oneline <base>..HEAD     # what actually landed
git diff --stat <base>..HEAD       # what it touched
```

Numbers for "how it ended" come from the test log in front of you, not from recollection.

## The session document

Permanent and immutable. Organise it around **decisions and measurements, not chronology** — the diff already holds the sequence.

Lead with the finding: *"The cutout stopped eating the character's white"* beats *"First I looked at X, then Y"*.

Three things earn their place:

- **Numbers instead of adjectives.** "102 of 219 assets had ≥2% of the drawing cut away" lets the next person tell whether their change helped; "many were broken" does not.
- **The wrong diagnoses that cost time.** "This looked like the discard threshold; instrumentation showed it never reached the extractor" stops someone walking the same path.
- **What you deliberately left out, and why.** This is what prevents it being re-litigated next month.

## The handoff document

One reader: whoever picks this up next. Front-load what stops them breaking something.

**"Left OPEN" is why this file exists.** Be specific, name the evidence, and say when something is the owner's call rather than an agent's:

> Three pacing tests are red. Proven pre-existing — they fail identically on clean `origin/main`, in a separate worktree. Recalibrating the ranges is the owner's decision, not an agent's.

versus the useless version: *"tests need work"*.

Also record **what must not be broken** — the load-bearing thing whose reason is invisible from the code — and any **tool you built** that the next agent would otherwise rebuild.

## Relays (Baton runs)

Every relay close produces all three outputs, plus the relay block in the run's journal. The block holds:

- what landed, with hashes;
- what is still red;
- the new traps;
- the line "subagents in flight: 0";
- a concrete "NEXT STEP:".

Two things never go in the handoff:

- **Owner requests and answers** live in the run's `OWNER.md`. The handoff is rewritten at every close and would lose
  them.
- **What only the owner can do** goes to `OWNER-TODO.md`, with the exact step.

## Common mistakes

| Mistake | Fix |
|---|---|
| Only writing the session doc | The general is the third output; without it the system stops working |
| Chronological narration | Lead with findings |
| "Everything works" | Name the numbers, including the red ones |
| Vague open items | Name the evidence and whose call it is |
| Versioning `handoffs/` | Keep it gitignored; only the newest matters |
| Restating the diff | If the code says it, do not repeat it |

## Red flags

- Writing from memory instead of from `git log` and the test output.
- About to call a suite green without a number in front of you.
- Calling a failure pre-existing without having verified it in isolation.
