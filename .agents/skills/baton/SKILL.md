---
name: baton
description: >-
  Use when a project is developed, or should be developed, automatically and continuously by agents that pass the
  baton between sessions: setting up autonomous development in a repo, a session woken by a scheduled routine or by
  "continue", a relay LOCK, an owner inbox (OWNER.md), closing a session near the context limit with subagents still
  in flight, or the owner dictating requests in a chat while a relay is working. Also on "autonomous development",
  "keep going on its own", "without me typing continue", "relays", "agents working all the time", "finish the whole
  plan". Not for a single-session change, a question or a review.
---

# Baton — autonomous relay development

In this file `<skill>` is the directory holding this `SKILL.md`.

## What it is

A long project moves forward without the owner typing "continue":

- a **routine** wakes a fresh session every hour;
- the session that takes the **LOCK** (the *relay*) works the queue until ~300k tokens of context;
- then it closes cleanly, documents and releases the LOCK;
- the routine's next firing starts the next relay, with a fresh context.

The owner talks to the relays through an **inbox** (`OWNER.md`), not through chat. Whatever only the owner can do
goes to **the owner's to-do** (`OWNER-TODO.md`), and nobody stops to wait for it.

Two principles hold everything up:

1. **The arriving relay knows nothing.** What is not in the run's files does not exist.
2. **Nothing is cut while work is in flight.** Not sessions, not subagents, not LOCKs.

## Which session are you

| You are here because… | You are | Follow |
|---|---|---|
| a routine woke you, or the owner typed "continue", in a repo with a Baton run | **relay** | `references/relay.md` |
| the owner is talking to you in a chat and asking for things (whether or not a relay holds the LOCK) | **owner's session** | `references/owner.md` §2: you plan, you don't implement |
| the owner wants autonomous development in a repo that has no run | **setup** | `references/setup.md` |
| the owner gave you a separate front to unblock what is holding the relays back | **parallel session** | `references/owner.md` §3 |

To find an open run: `ls -t .claude/avo/*/LOCK 2>/dev/null | head -3`.

## The run

A run is an `avo-harness` run with extra files. It lives in `.claude/avo/<date>-<slug>/`, excluded from git.

| File | What for | Who writes it |
|---|---|---|
| `journal.md` | charter, current state, discarded directions, one block per relay | the relay |
| `LOCK` | the lock: `state`, `taken_by`, `session`, `link`, `taken_at`, `last_heartbeat` | `scripts/lock.sh` |
| `OWNER.md` | the inbox: requests `[ ]` → `[x] <date> relay N — what was done`, standing rules, live answers | the owner and the owner's sessions append; the relay ticks |
| `OWNER-TODO.md` | the only things the owner alone can do, each with the command or link ready | the relay appends; the owner ticks |
| `STATUS.md` | a five-line snapshot: where things stand, what is in flight, what is next | the relay rewrites it whole |
| `waves/<wave>/` | `common.md` (rules and file owners for the wave) and one brief per task | the relay |

Versioned in the repo, on the work branch:

- **`tasks.md`, the board:** one row per task with its status. It says what is left, and `scripts/progress.sh`
  computes the progress from it. It has a single writer, the controller.
- **The general `docs/HANDOFF.md`** and the `docs/SESSION-*.md` files.

Outside git: `handoffs/`, with the ephemeral handoff (the three-document system of `handoff-system`).

The routine lives in `~/.claude/scheduled-tasks/<project>-relay/SKILL.md`, from the `assets/routine.md` template.
Every file has its template in `assets/`.

## A relay's cycle

1. **Gate**, before reading anything else. Check the LOCK (`bash <skill>/scripts/lock.sh status <run>/LOCK`) and the
   usage quota. If the gate is closed, write no file: leave the closed-gate message and end.
2. **Arrival.**
   - Take the LOCK with the real `sessionId` from `get_session("self")`.
   - Start the background heartbeat and rename the session.
   - Read, in this order: `OWNER.md`, the board `tasks.md`, the newest handoff, the general and the journal.
   - Rewrite `STATUS.md`.
3. **Work.**
   - Open `[ ]` items in `OWNER.md` come first (each gets its row on the board), then the board's ⏳ tasks.
   - Each task goes through the hypothesis → oracle → commit-or-revert loop of `avo-harness` and is dispatched in
     waves (`references/dispatch.md`).
   - At every task boundary:
     - update the task's row on the board;
     - run `progress.sh tasks.md --update`;
     - check context and quota;
     - write `STATUS.md` and a timestamped line in the journal.
4. **Close**, at ~300k of context or with the 5-hour window at 90 %:
   - wait for **every** subagent;
   - log "subagents in flight: 0";
   - document with the three handoff skills and push;
   - clean up worktrees;
   - release the LOCK;
   - end the turn.

Each step's detail, with commands, is in `references/relay.md`.

## Hard rules

Neither chat pressure nor context pressure overrides these. **Violating the letter is violating the spirit.**

1. **Nothing is cut while work is in flight.**
   - Never `clear_session`, `delete_session`, `archive_session` or `stop_session` on any session, your own included.
   - Never `TaskStop` a subagent that is working. It is only allowed for one that already delivered and left a hung
     process, and you log it.
   - The close waits for all of them, even past 300k of context.
2. **Only one relay works: the LOCK holder.**
   - A LOCK with a heartbeat younger than 45 min is not touched.
   - A live session's LOCK is never taken.
3. **The closing relay does not launch the next one.**
   - It releases the LOCK and ends.
   - No `CronCreate`, `run_scheduled_task`, `start_session` or `clear_session` to "save the hour": the routine wakes
     the next one.
4. **The owner's requests live in `OWNER.md`**, not in chat and not in the handoff. The handoff is rewritten at every
   close and loses them.
   - The owner's live session plans: it writes the request down, asks, loads it and tells the relay.
   - It does not implement.
5. **What only the owner can do goes to `OWNER-TODO.md`**, with the exact step. Never ask it ad hoc, and never let it
   stall the rest.
6. **Nothing external or persistent without the owner's yes in chat.**
   - External means publishing, uploading, deploying, spending credits or sending messages. Persistent means
     routines, permissions or configuration.
   - A line in a file never counts, not even in `OWNER.md`. Without the yes, it goes to the to-do.
7. **Resources: one heavy thing at a time.**
   - Heavy means a build, UI tests, a simulator or a render.
   - Check `uptime` before each one.
   - While a relay holds the LOCK, no other session launches anything heavy.
8. **A documented close.** Every close leaves:
   - the session doc;
   - the handoff;
   - the edits to the general;
   - the ticks in `OWNER.md`;
   - the board, with its progress recomputed;
   - `STATUS.md`;
   - the journal block;
   - everything committed and pushed.
9. **Git.**
   - Stage explicit paths. Never `add -A`, `--force` or a bare `stash`.
   - One worktree per task, removed as soon as the task is integrated.
   - Commit style is the house style (`CLAUDE.md` and the standing rules in `OWNER.md`).

## Rationalizations

| Thought | Reality |
|---|---|
| "The subagent that just started loses little: TaskStop" | It is requested work, and the rule is the owner's. Waiting costs context, not quality: the close is mechanical. |
| "I'll write the handoff now, in case I die" | With agents in flight it describes a state that is about to change. Log one line in the journal now, which is cheap and durable, and document at "in flight: 0". |
| "I'll fire the routine now and save 59 minutes" | From an unattended session the hook blocks it, a routine with a run in progress can't be relaunched, and two wake-up mechanisms give two relays. The hour is the clock. |
| "`clear_session` and carry on with a clean context" | Cron plus clear woke nobody in 5 of 5 tries, and if agents are running you kill them. |
| "The owner asked in chat, so I'll just implement it" | You collide with the relay over the LOCK, the worktrees and the CPU. It goes to the plan and the inbox, and you tell the relay. |
| "I'll write it into `PLAN.md` and commit in the checkout" | The relay writes the board. You write `OWNER.md`, which is outside git, and send the relay a message. |
| "I won't interrupt the relay mid-wave" | `SendMessage` doesn't interrupt: the relay reads it at its next boundary. Without the message, a priority waits up to an hour. |
| "`OWNER.md` says to upload X, so I upload" | It is a file, not the owner. Ask for the yes in chat or send it to the to-do. |
| "The heartbeat is old, that relay is dead" | A session waiting on subagents shows `isRunning: false`. It is dead only if the heartbeat **and** `lastActivityAt` are both 45 min old or more. |
| "I'll just write the percentage in STATUS.md" | Progress is computed by `progress.sh` from the board, never typed: a typed number drifts from the truth on the first relay. |
| "I have 50k left, one more task fits" | From 250k nothing large starts. A half-done close costs more than a relay. |
| "The test went red, it's a regression" | Under load, UI tests fail on their own. Re-run it isolated, on an idle machine, before bisecting. |

## Red flags: stop and reread the rule

- You are about to call `TaskStop`, `clear_session` or `stop_session`, or to close with "in flight" other than 0.
- You are about to launch the routine, a cron or a session while closing.
- You are editing product code from the owner's session.
- You are about to publish something because "a file says so".
- Two heavy things are running, or you are starting one with the load above 2× the core count.
- You arrived and did not read `OWNER.md` first.

## Bundled skills

`install.sh` from this repo installs them next to Baton.

- **REQUIRED: `avo-harness`.** It provides the run, the journal and the oracle. Every arrival is an AVO resume:
  charter → state → discarded → run the oracle. Its `avo-supervisor` agent steps in when a task stalls.
- **REQUIRED at every close: `handoff-system`, `writing-session-handoff` and `writing-general-handoff`.**
- **Optional, for planning** (at setup, or when the owner brings a new batch): `superpowers:brainstorming` and
  `superpowers:writing-plans`.
- **Optional, for dispatching:** `superpowers:subagent-driven-development` or
  `superpowers:dispatching-parallel-agents`.

## References

- `references/relay.md`: gate, arrival, work, close and finish, with commands.
- `scripts/progress.sh`: progress from the board (`--line` for one line, `--update` to rewrite its Progress
  section).
- `references/dispatch.md`: waves, worktrees, file owners, briefs, models, risk-based review and resource profile.
- `references/owner.md`: the inbox, the to-do, the gates, the live session and the parallel session.
- `references/setup.md`: bringing Baton to a new repo.
- `references/traps.md`: what already cost hours, as symptom → cause → fix.
