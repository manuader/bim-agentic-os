# Setup: bringing Baton to a new repo

You are here because the owner wants a project to develop itself and the repo has no Baton run.

## 0. Does it fit?

It takes all three:

1. **The work spans sessions.** It is a plan of many tasks or a whole release, not a single change.
2. **There is an oracle, or one can be built.** That means a command that exits 0 when the work is done. Without an
   oracle, relays have nothing to land on, so building it is task 1.
3. **The owner wants it to go on without them**, and accepts talking to the relays through the inbox.

If any is missing, say so and offer what does fit: a single session with `avo-harness`, or a plan to execute live.

## 1. The interview

First work out what you can on your own:

- commit style, language and trailers, from `git log`;
- the branches;
- `CLAUDE.md`;
- the test commands;
- the core count and memory.

Then ask the rest with `AskUserQuestion`: four questions at most per round, with options and your recommendation
first. The answers go into the journal's charter and into "Standing rules" in `OWNER.md`.

| Topic | What gets decided |
|---|---|
| **Done** | what "finished" means, in one sentence, and the command that checks it |
| **Branches** | the work branch, which branches are never touched (e.g. `main`), when to merge |
| **House** | language of commits and docs, commit style, co-author trailer yes or no, code style |
| **Protected areas** | files or areas never touched without notice (money, persistence, monetization…) |
| **External** | what is pre-authorized (uploading builds, deploying to staging) and what needs a yes every time (publishing, paying, submitting for review) |
| **Gates** | what the owner approves before work goes on, and how they want to see deliverables |
| **Resources** | cores, RAM, how long a full build takes, simulators or not. The profile comes from this (`dispatch.md` §1) |
| **Pace** | how often the routine fires (default: hourly), and the context thresholds (default: 250k and 300k) |
| **When done** | does the routine disable itself? Is there a next phase? |

Whatever you assume without asking goes into "Standing rules", where the owner can correct it.

## 2. The plan

- Use `superpowers:brainstorming` for the what and `superpowers:writing-plans` for the how, if they are installed.
- Every task in the plan carries:
  - its "Done when", which a command can check;
  - its dependencies;
  - the files it touches.
- Any task that no command can check gets "build its check" as its first step.
- Load the tasks into the **board**, `tasks.md` at the root of the work branch, from `assets/tasks.md`:
  - one row per task: ID, task, status, dependencies, files it owns, "Done when";
  - grouped by phase, under `### <phase>` headings;
  - status ⏳ if it has no pending dependency, ⛔ otherwise.

  Then run `bash <skill>/scripts/progress.sh tasks.md --update` and commit it. From then on the controller is its
  only writer, and the progress always comes from `progress.sh`.
- The plan is versioned in the repo, on the work branch. The reasoning behind its decisions goes into this planning
  session's doc.

## 3. The run

```bash
R=<repo>; RUN=$R/.claude/avo/$(date +%F)-<slug>
mkdir -p "$RUN/waves" "$R/handoffs"
grep -qxF '.claude/avo/' "$R/.git/info/exclude" 2>/dev/null || echo '.claude/avo/' >> "$R/.git/info/exclude"
grep -qxF 'handoffs/' "$R/.gitignore" 2>/dev/null || echo 'handoffs/' >> "$R/.gitignore"
S=<skill>/assets
cp "$S/OWNER.md" "$S/OWNER-TODO.md" "$S/STATUS.md" "$RUN/"
cp <skills dir>/avo-harness/assets/journal.md "$RUN/journal.md"
bash <skill>/scripts/lock.sh create "$RUN/LOCK"
```

1. **The journal.** Fill in the charter:
   - goal, oracle and budget;
   - constraints, including the owner's rules and the resource profile;
   - one line: "Relay at ~300,000 tokens of context; see the baton skill".
2. **The general.** If the repo has no `docs/HANDOFF.md`, create it with `writing-general-handoff`.
3. **`OWNER.md`.** Write what came out of the interview into "Standing rules".
4. **The `.gitignore`.** Commit it on the work branch, with explicit paths.

## 4. The routine

1. **Fill in `assets/routine.md`:** project, repo, work branch, run, oracle, gates, house rules and finish. Delete
   whatever doesn't apply.
2. **Ask for the owner's yes in chat**, since it is persistent configuration. Show them the name, the schedule and
   the prompt. "Set it up and start now" is not a specific yes for the routine.
3. **With the yes, create it with `create_scheduled_task`:**
   - name `<project>-relay`;
   - cron `0 * * * *`, or whatever the owner chose;
   - the filled-in text as its prompt.
4. **Tell the owner what a routine cannot do**, because it runs without them:
   - approve Computer Use;
   - make external writes the classifier considers unrequested;
   - run `sudo`.

   All of that goes to the to-do, which is why the to-do exists.
5. **To change an existing routine,** use `update_scheduled_task` or edit its `SKILL.md`. Add an "UPDATE" block on
   top, and get the owner's yes for this too.

## 5. The first relay

- **If planning left plenty of context (under ~150k),** this session is relay 1: take the LOCK (`relay.md` §2) and
  start.
- **If not,** close the planning with the handoff skills, leave the LOCK FREE, and let the first firing take it.
  Planning a large plan usually ends past the threshold; that's normal.
- **Never start the first relay early with `run_scheduled_task` or `start_session`.**

## 6. Memory

Save a short pointer in the project's memory:

- where the run is and what the routine is called;
- that the project runs on Baton;
- that requests go to `OWNER.md` and are not implemented in the owner's chat.

That way a new session of the owner's knows to plan instead of implementing.
