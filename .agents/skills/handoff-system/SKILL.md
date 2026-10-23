---
name: handoff-system
description: Use when an agent joins a codebase and cannot tell what is already decided, when work spans multiple sessions or multiple agents, when the same mistake gets made twice, when onboarding means reading months of history, or when setting up documentation so future agents pick up cleanly
---

# Handoff System

## Overview

A codebase accumulates two kinds of knowledge: **what is still true** (architecture, rules, decisions already closed, traps already paid for) and **where we stopped** (what is half-done, what is broken, what to do next). Cramming both into one file makes it unreadable; scattering them across chat logs makes them unfindable.

This system splits them into three documents with different lifetimes, so a fresh agent reads **two files** and knows where it stands.

In Baton runs, every relay close writes all three, and owner requests live in the run's `OWNER.md`, never in the handoff (see writing-session-handoff).

## The three documents

| Document | Contains | Lifetime |
|---|---|---|
| `docs/HANDOFF.md` — **the general** | what stays true: architecture, repo rules, closed decisions, traps already paid, one entry per session | permanent, **accumulates** |
| `docs/SESSION-<date>-<topic>.md` | the detail of ONE session: what was asked, measured, decided, and why | permanent, **never edited after** |
| `handoffs/HANDOFF-<date>-<topic>.md` | state at close: what is open, in flight, and must not break | ephemeral, **gitignored** |

The split is the point. The general answers *"where does this project stand?"*. The session doc answers *"why is it like this?"*. The handoff answers *"what do I do now?"*.

## The protocol

**Arriving:** read the general and the newest file in `handoffs/`. Two files. Do not read every session doc — they are archives you consult when the general points at one.

**Closing:** three things, in this order.

1. Write your `docs/SESSION-…` — the detail and the *why*.
2. Write your `handoffs/HANDOFF-…` — what is open for whoever is next.
3. **Update the general** — new entry at the top of the sessions section, plus anything that belongs in decisions, traps, or the doc map.

Step 3 is the one that gets skipped and the one that holds the system up. Without it the general rots, the next agent reads twelve session docs to reconstruct the present, and the system has bought nothing.

## Which skill to use

| You are... | Use |
|---|---|
| setting the system up in a repo that has none | `references/installing.md` |
| creating the general for the first time, or updating it | **writing-general-handoff** |
| finishing a stretch of work | **writing-session-handoff** |

## The golden rule for everything you write here

**Record what cost time and is not derivable from the code.**

The numbers that calibrated a decision. The diagnosis that turned out wrong before the right one. The order operations must happen in. The trap that ate an afternoon.

What the code already says, do not restate — it drifts out of sync and then competes with the source of truth. A doc that repeats the code is worse than no doc, because someone will trust the stale copy.

## Staleness is the failure mode

These documents are believed. That is their value and their danger.

- If you find a fact in the general that is no longer true, **fix it** — do not work around it. A general that says the price is 50 when it is 25 sends the next agent to argue with reality.
- Before acting on a handoff, check it is still current: read the git log since it was written. A handoff that predates three merges describes a project that no longer exists.
- Never paste secrets, tokens, or credentials into any of these. Handoffs get shared and pasted into prompts.

## Honesty is load-bearing

Write what is actually broken, skipped, or unverified. A handoff that claims "all green" with three failing tests teaches the next agent to distrust the whole document — and then none of it works, including the parts that were true.

If tests fail, say which and whether they failed before your change. If you did not verify something, say so rather than implying you did.

## Common mistakes

| Mistake | Why it breaks |
|---|---|
| One giant document for everything | Nobody reads it, so nobody updates it |
| Writing the session doc but not updating the general | The general rots; the next agent reads everything |
| Narrating what you did, chronologically | Nobody needs your play-by-play; they need the conclusions |
| Restating what the code says | Drifts, then competes with the truth |
| Versioning the ephemeral handoffs | They pile up as noise; only the newest matters |
| "See the git history for details" | The history is the raw material, not the answer |
