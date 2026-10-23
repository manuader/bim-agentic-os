# Templates: session document and handoff

Write in the language the repo uses. Delete sections that do not apply — an
empty heading is worse than no heading.

---

## `docs/SESSION-<date>-<topic>.md`

Permanent, never edited after. Organised by decisions, not by chronology.

```markdown
# Session <date> — <what changed, stated as a claim>

Continues <previous session doc>, if it does.

## What was asked
The request in the requester's own terms, with the constraints they set.

## The cause
For a fix: what was actually wrong, with the evidence that proved it. Include
the diagnoses that turned out wrong when they cost time — that is what stops
the next person walking the same path.

## What was measured

| | value |
|---|---:|
| <what> | <number> |

Numbers, not adjectives. They let the next person tell whether their change
helped.

## What was built
The shape of the solution and WHY that shape. The alternative that was
considered and rejected, with its reason.

## What was left out
Deliberately, and why. This is what prevents someone re-litigating it in a month.

## How to run it
Exact commands.

## How it ended
Real numbers, including what is still red and whether it was red before.
```

---

## `handoffs/HANDOFF-<date>-<topic>.md`

Ephemeral, gitignored. One reader: whoever picks this up next.

```markdown
# Handoff <date> — <topic>

Versioned doc: `docs/SESSION-<date>-<topic>.md`
Pushed tip: `<commit>`

## Status
One paragraph. Done, or done-except-X.

## Left OPEN
1. **<thing>** — what is known, what the next step would be, and whether it is
   a decision for the owner rather than for an agent.

## Do not break
What is load-bearing and non-obvious — the thing whose reason is invisible from
the code.

## Traps that cost time
The ones not yet folded into the general.

## New tools
Anything added that the next agent would otherwise rebuild.
```

---

## Gathering the evidence first

```bash
git log --oneline <base>..HEAD          # what actually landed
git diff --stat <base>..HEAD            # what it touched
git log --oneline <base>..origin/main   # what arrived while you worked
```

Numbers for "How it ended" come from the test log, not from memory.

---

## Two entries worth imitating

**An open item that is actually useful** — it names the evidence and whose call
it is:

> Three pacing tests are red. **Proven pre-existing**: they fail identically on
> clean `origin/main`, in a separate worktree with its own simulator, with none
> of this branch's code. Recalibrating the ranges is the owner's call, not an
> agent's. The diagnosis, for whoever does: the report gives `godWall: nil` and
> `finalMaxTier: 12`, so the simulation stalls before the end.

**A trap with its measurement** — symptom, number, cause, consequence:

> **`--retries N` means N+1 generations** (`range(retries + 1)`). Multiplied by a
> driver that also retried, that was four per asset: 344 instead of 86.
