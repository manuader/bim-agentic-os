# The relay: gate, arrival, work, close

You are here because a routine woke you, or because the owner typed "continue", in a repo with a Baton run. In the
commands, `<run>` is `.claude/avo/<date>-<slug>` and `<skill>` is the skill directory. Use absolute paths.

## 1. Gate

Do this before reading anything else. If it says no, **write no file** and end with the closed-gate message (§1.1).

1. Run `bash <skill>/scripts/lock.sh status <run>/LOCK`. It prints the state, the holder, the `session` and the
   minutes since the last heartbeat.
2. If the LOCK is **FREE**, go to step 4.
3. If it is **TAKEN**:
   - with a heartbeat younger than 45 min, another session is working: the gate is closed.
   - with a heartbeat of 45 min or more, call `get_session(<session>)`.
     - A session waiting on subagents can show `isRunning: false` and still be alive. Treat it as dead only if its
       `lastActivityAt` is also 45 min old or more.
     - If it is alive, or you are unsure, the gate is closed. Do not touch it.
     - If it is dead, go on. Log it as a stale-lock takeover, and check what it left half-done: worktrees, builds,
       unpushed merges, processes.
4. Call `get_usage`. If the 5-hour window is at 85 % or more, or the weekly one at 95 % or more, the gate is closed.
   If usage can't be read, go on.

### 1.1 Closed-gate message

The owner needs to know, in one tap, which chat is the working one. Call `get_session` on the LOCK's `session:`. It
is read-only. Then end with exactly these three lines:

```text
🔒 Not started: the LOCK is held by **<taken_by>** → [<title of that session>](#<session>)
claude://claude.ai/epitaxy/<session>
taken <taken_at> · last heartbeat <last_heartbeat> (N min ago) · <working | waiting on subagents | idle for N min>
```

- If you left because of the quota, say how far along it is and still add the three lines. If the LOCK is free, say
  "LOCK FREE" instead.
- If `get_session` doesn't find that id, say "the LOCK points to <id>, which the app doesn't know", plus `taken_by`
  and the heartbeat.
- Rename **this** session, and no other: `set_session_title("self", "Relay — waiting (held by <taken_by>)")`.

## 2. Arrival

1. **Number.** N is the last `## Relay N` in the journal, plus one.
2. **LOCK.**
   - Call `get_session("self")`. Its `sessionId` (the app's `local_…`) is the only valid id for `session:`. With any
     other id, the next relay can't show the owner the link to your chat.
   - Take the LOCK:

     ```bash
     bash <skill>/scripts/lock.sh take <run>/LOCK "relay N (routine <name>)" <sessionId>
     ```

   - If it refuses, someone got there first: go back to the gate.
3. **Heartbeat.** Start it with Bash in the background (`run_in_background`):

   ```bash
   bash <skill>/scripts/lock.sh beat-loop <run>/LOCK <sessionId> 10
   ```

   The loop ends on its own once the LOCK is no longer yours. Without a heartbeat, a relay waiting on subagents looks
   dead and another one can take its LOCK.
4. **Title.** If the tool exists: `set_session_title("self", "Relay N — ACTIVE (holds the LOCK)")`.
5. **Journal.** Append the block `## Relay N — <date and time>`. It says what woke you (the routine, the owner, a
   stale LOCK), plus quota and context on arrival. Always append through Bash (`>>`). Never rewrite the journal.
6. **Reading,** in this order:
   1. `OWNER.md`: every `[ ]` is an owner request, and the "Standing rules" override any default;
   2. the board `tasks.md` on the work branch, to see what is left. Get the numbers with
      `bash <skill>/scripts/progress.sh tasks.md`;
   3. the newest handoff in `handoffs/`;
   4. the general `docs/HANDOFF.md`: the note at the top, and whatever concerns you;
   5. the journal: charter, current state, discarded, and the last relay block;
   6. `git log --oneline` and `git status` since the handoff's date, on the work branch;
   7. the run's oracle, in its cheap mode if it has one, to know where things stand today.
7. **Status.** Rewrite `STATUS.md` (template: `assets/STATUS.md`). Its progress line is
   `bash <skill>/scripts/progress.sh tasks.md --line`, pasted as is.

## 3. Work

- **Order:**
  1. the `[ ]` items in `OWNER.md` that aren't waiting on an owner gate. Each one gets its row on the board
     ("Owner requests added by relays"), numbered after reading the board and `OWNER.md`, so IDs never collide;
  2. the journal's "Next step";
  3. the board's ⏳ tasks, in the plan's order (`progress.sh` lists the next ready ones).
- **Each task** goes through the loop: hypothesis → variants → action → oracle → commit or revert. Use
  `avo-harness`, and dispatch per `references/dispatch.md`.
- **At every task boundary:**
  - read the context (`get_usage("self").context.tokensUsed`) and the quota;
  - update the task's row on the board (`references/dispatch.md` §7 has the transitions), then
    `bash <skill>/scripts/progress.sh tasks.md --update`;
  - tick the box in `OWNER.md`: `[x] <date> relay N — what was done`, deleting nothing;
  - append a timestamped line to the journal;
  - rewrite `STATUS.md`.
- **Thresholds:**
  - from **~250k**, start no new large task;
  - at **~300k**, or with the 5-hour window at 90 %, start the close.
- **Owner gates.** If a task needs the owner:
  - its row goes to 🔒, and the item goes to `OWNER-TODO.md` with the exact step, and to the journal;
  - you go on with something else;
  - if everything left waits on the owner, don't invent work. Log it, tell the owner (`PushNotification`, if
    available) and close.
- **The owner in your chat:**
  - answer, then log it in `OWNER.md` under "Live answers from the owner", with the time and the owner's exact
    words;
  - when the owner is present, ask with `AskUserQuestion`: that answer is the yes in chat.
- **The owner says "stop":**
  - kill only your own processes, by PID;
  - shut down your simulators or emulators;
  - log "WHERE IT STOPPED" in the journal and wait;
  - a "continue" lifts it.

## 4. Close

In this order, skipping nothing:

**a.** Stop dispatching. Wait until **every** subagent and background task has finished, even past 300k of context.
Integrate what they deliver, or log it for the next relay.

**b.** Log in the journal: "subagents in flight: 0".

**c.** Document and commit:

- `writing-session-handoff`: the session doc `docs/SESSION-<date>-<topic>.md` and the handoff
  `handoffs/HANDOFF-<date>-<topic>.md`;
- `writing-general-handoff`: a new entry at the top of the general, plus decisions, traps and the docs map if they
  changed;
- the board, with every row current and its progress recomputed (`progress.sh tasks.md --update`);
- the ticks in `OWNER.md`, and whatever is new in `OWNER-TODO.md`;
- the commit, with explicit paths, and the push of the work branch.

**c2.** Clean up:

- integrated worktrees: run `bash <skill>/scripts/clean-worktrees.sh <repo>` without `--apply` first, to see the
  dry run;
- old oracle logs;
- your simulators and processes.

**d.** Release:

- rewrite `STATUS.md` one last time;
- run `bash <skill>/scripts/lock.sh release <run>/LOCK <sessionId>` (the heartbeat stops by itself on its next
  cycle);
- in the journal: "CLOSE … LOCK released <time>", plus a concrete **"NEXT STEP:"** line;
- set the title to "Relay N — closed".

**e.** End the turn. Do **not** launch another routine, do **not** use `CronCreate`, do **not** open sessions, and do
**not** clear or delete your own. The next hourly firing starts the next relay.

### The relay block in the journal

```markdown
## Relay 91 — 2026-10-22 18:16:52 -0300
Woken by routine <name> (hourly firing). LOCK FREE (relay 90 released it at 18:15). Quota 5h 4 %, weekly 13 %.
- 18:20 Arrival: C1–C6 done; first box with no owner gate = C7. Agent c7-features dispatched (wt r91-c7-features).
- 18:27 c7-features delivered (54bcfa27). Merged into the work branch. Subagents in flight: 0.
- 19:46 C7 GREEN (14/14). The external upload was blocked by the classifier → to OWNER-TODO (one command).
- CLOSE a) no dispatches b) **subagents in flight: 0** c) session doc + handoff + general + OWNER; branch fd10fbf8 pushed c2) no worktrees left d) LOCK released 19:48:28.
**NEXT STEP:** C13 (…), per handoffs/HANDOFF-2026-10-22-relay-91.md.
```

Each line is a fact with a time, a hash or a number, not a story. Anything that narrows the search also goes to
"Discarded" (format in `avo-harness`).

## 5. Finish

The work is done when all three hold:

- the board shows every task ✅ or ⏭️ (`progress.sh` says N/N);
- the run's oracle is green;
- `OWNER-TODO.md` holds only the owner's items.

Then:

1. Write a report: what landed (the board's final progress), the evidence (command and output) and what is left for
   the owner. Publish it as an
   Artifact if setup planned for that.
2. Notify the owner with `PushNotification`, if available, and in your final message.
3. If the routine authorizes it in writing ("disable when done"), disable it with `update_scheduled_task`
   (`enabled: false`). If not, ask the owner to do it, since it is persistent configuration.
4. If `OWNER.md` has a next phase loaded, don't disable anything: the next relay carries on with it.
