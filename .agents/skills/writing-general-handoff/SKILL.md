---
name: writing-general-handoff
description: Use when a repo has no single document explaining where it stands, when onboarding an agent means reading months of git history, when decisions get re-litigated because nobody recorded them, when the same trap is hit twice, or when a session ends and the project-level document needs its new entry
---

# Writing the General Handoff

## Overview

The general (`docs/HANDOFF.md`) is the one permanent document a new agent reads to know where a project stands. Every session adds an entry; anything that turns out to be durable — a decision, a trap, a rule — gets folded in.

**REQUIRED BACKGROUND:** the three-document model, the protocol and the golden rule are in **handoff-system**.

Two jobs use this skill: **building** it the first time and **updating** it when a session ends. Same sections, different amount of writing.

Section-by-section skeleton with worked examples: `references/template.md`.

## Building it: reconstruct from evidence

Do not write it from what someone tells you the project is. Read, in this order:

1. **The tests.** The best source by far. A test that pins a value records a decision *and* proves someone cared enough to protect it; its comments often carry the reasoning outright.
2. **`git log`**, especially merges and any message that explains a *why*.
3. **Code comments** that give rationale rather than mechanics.
4. **Existing docs** — as claims to verify, not facts. They are usually stale; that is typically why you are here.

When a doc and the code disagree, the code wins and you fix the doc.

## The sections

```
§0 How to use this doc   §5 Decisions not to re-litigate
§1 What this is          §6 How to verify
§2 Repo rules            §7 Traps we already fell into
§3 Architecture          §8 What's left
§4 Sessions, newest first §9 Document map
```

**§0 must state the protocol**, which is what makes the system self-perpetuating: arrive by reading this file plus the newest handoff; close by writing a session doc, a handoff, and updating this file. Say plainly that the last step is the one that gets skipped and the one holding everything up.

Three sections carry most of the value:

**§5 — decisions with their reason.** The reason is not decoration: without it the next agent reasonably reopens the question, which is the exact cost you were avoiding.

**§7 — traps as symptom → measured cause → consequence.** A number beats three paragraphs of advice:

> **The generation timeout cannot go below ~250 s.** That is the measured floor. A 200 s cap kills the image seconds before it renders, and since no file is saved the asset never leaves the queue — it regenerates forever.

**§6 — commands that actually run**, in order, with expected numbers ("unit **380**, before the UI suite"). Numbers let the next agent tell "I broke this" from "this was already red". Include ordering constraints and why they exist.

## Updating it at the end of a session

Four edits, none optional:

1. **§4** — new entry on top: what changed and what a reader needs without opening the session doc. Link it.
2. **§5** — did anything become settled? Add it *with its reason*.
3. **§7** — did you hit a trap? Add it, with the measurement.
4. **§9** — add your session doc and what it answers.

Then re-read what you touched. **If you find a stale fact, fix it** — you are the last person who will know it is wrong.

## Common mistakes

| Mistake | Fix |
|---|---|
| Writing it from the README | Read the tests and `git log` |
| §4 entries that narrate hour by hour | Say what *changed*, not what you did |
| Decisions without reasons | They get reopened — the whole cost you were avoiding |
| Traps as vague advice | Give the symptom and the number |
| Leaving a stale fact because it is not yours | A believed document that is wrong is worse than none |
| Letting it grow without pruning | Cut what stopped being true; being right is the metric, not length |

## Red flags

- About to write "see the git history for details" — that is raw material, not an answer.
- Copying a code snippet the code already contains — it will drift.
- Cannot state the *reason* for a decision you are recording — find it or leave it out.
