# Dispatch: waves, worktrees, briefs and resources

The relay is the **controller**. It dispatches, reviews, integrates and documents, and writes no product code. If a
build fails, an agent fixes it, not the controller by hand. That way the controller's context lasts for many tasks.

## 1. Resource profile

Decided at setup and written into the journal's charter. It changes everything below.

| Profile | When | How work is verified |
|---|---|---|
| **Scarce** | heavy builds (Xcode, Android, large C++ or Rust, a monorepo), or a machine with ≤ 16 GB | Agents **neither build nor run tests**: they write code and tests, commit and hand back. The controller integrates the whole wave and builds and tests **once**. If something fails, it dispatches an agent to fix it. Exception: cheap tests that don't spin up the heavy build. |
| **Ample** | fast tests, builds in seconds, cheap CI | Each agent runs its task's oracle before handing back. The controller runs the fast oracle once per wave. |

Both profiles:

- **One heavy thing at a time** on the machine: a full build, UI tests, a simulator, a render, a recording.
- **`uptime` before every heavy task.** If the 1-minute load is above 2× the core count (`sysctl -n hw.ncpu` or
  `nproc`), wait with a Monitor or an until-loop. Never launch on top of it.
  - Measured: three builds at once pushed the load to 346 on 8 cores, and tests hung.
  - A full oracle run under load took 20 h, and its result was worthless.
- **At most 2–3 agents at a time.** The bottleneck is the CPU, not the agent count.
- **Each agent gets its own** simulator, emulator, port, test database, dependency environment and build directory.
  Two runs on the same simulator, or two suites on the same test database, step on each other.
- **Shut simulators and emulators down** as soon as you are done.
- **Kill only your own PIDs.** Never `pkill -f` on a generic name, and never `while pgrep -f …`: it matches itself.

## 2. Waves and file owners

A **wave** is a batch of independent tasks dispatched together.

- **One owner per file per wave.** Build the table in `waves/<wave>/common.md` (template: `assets/wave-common.md`).
  An agent that needs a file from another row doesn't touch it: it notes it in its report, and the controller sorts
  it out when integrating.
- **Hot files** have a single owner per wave: the central state, the root view, the project file, the strings
  catalog. If two tasks need new strings, each hands back a snapshot of its keys, and the controller applies them
  when integrating.
- **The owner's protected areas** are not touched without notice, or without the owner's yes if the owner asked for
  that. Typically: persistence, money, monetization, and whatever `OWNER.md` lists.

## 3. One worktree per task

```bash
git -C <repo> worktree add -b <prefix>/<task> <repo>/.claude/worktrees/<prefix>-<task> <work-branch>
```

- **Always branch from the work branch**, not from the main checkout's HEAD. `Agent(isolation: "worktree")` branches
  from the main checkout's HEAD, so it only works if that HEAD is the work branch. Check before using it.
- Give the agent the absolute path, and the rule not to leave it.
- **Use a relay-specific prefix** (for example `r91/`), different from the parallel sessions' prefix, so the cleanup
  never deletes someone else's work.
- **Generated files:** in projects that generate them (`xcodegen`, `npm install`, a `.venv`), the new worktree
  generates or links them before its first build.
- **If you move a worktree, clear the build caches:** they keep stale absolute paths.

## 4. The brief

The agent **does not read the whole plan**: only its section, the wave's common rules and its file-owner row.
Template: `assets/brief.md`. A brief always states:

- the worktree, the branch and the base (hash);
- what to do, and where its "Done when" is;
- the files it owns;
- how it verifies, per the profile (scarce: "DO NOT BUILD");
- "before handing back, commit everything; leave no uncommitted changes or stray files", plus the house commit
  style;
- the report: what changed, the tests written, the risks, what the controller must run, the final hash, and the
  status (`DONE`, `DONE_WITH_CONCERNS`, `NEEDS_CONTEXT` or `BLOCKED`);
- if it produces something the owner approves (a script, a render, an image): the checkpoints where it stops and
  tells the controller with `SendMessage`, so the controller can show it to the owner.

## 5. Models and risk-based review

| Task | Implementer model | Review |
|---|---|---|
| a plan that already contains the code; mechanical work, tests, docs | sonnet | none: the controller reads the diff |
| normal | sonnet | sonnet reviewer |
| high risk: persistence and migrations, money and purchases, concurrency or the main loop, security | opus | opus reviewer |
| epic plans, design, research | opus | the controller reads the plan |

An opus review cost about 130k tokens. Keep it for where a mistake is expensive.

## 6. Integration

1. When a report arrives, check that the diff stays inside the agent's row.
2. Merge into the work branch, one task at a time.
3. At wave close, build and run the oracle once. In the scarce profile, this is the first build.
   - To see every error at once, build with the toolchain's keep-going option, if it has one.
   - Whatever fails goes to an agent.
4. Before calling a red UI or flaky test a regression, re-run it **isolated, on an idle machine**. Under load, tests
   fail on their own: 14 out of 14 reds once passed in isolation.
5. If a red reached the work branch, revert first and fix after.
6. As soon as a task is integrated, remove its worktree with `git worktree remove` (no `--force`) and its branch with
   `git branch -d`. Measured: 41 worktrees took 47 GB.

## 7. The board through a task's life

The controller is the board's only writer. Subagents report, and the controller records it.

| Moment | Row | Notes column |
|---|---|---|
| all deps ✅ and no agent in flight owns its files | ⛔ → ⏳ | |
| dispatched | ⏳ → 🔄 | agent name and worktree |
| handed back, reviewed, waiting for the wave's build or for integration | 🔄 → 🟢 | the agent's hash |
| red in review, in the build or after integration | → 🔧 | the failure, in one line |
| merged into the work branch and the oracle green | → ✅ | the integration hash |
| needs the owner | → 🔒 | pointer to the `OWNER-TODO.md` item |
| the owner drops it | → ⏭️ | why and when (only the owner drops tasks) |

After every change, run `bash <skill>/scripts/progress.sh tasks.md --update`. The board is committed with the work
branch at every wave close and at the relay's close.

## 8. Parallel sessions

- **Inside the relay:** 2–3 agents at most, on disjoint files.
- **With another session in the same checkout** (the owner's, or a parallel one):
  - every build runs in an isolated worktree, even if `git status` looks clean. The other session edits inside your
    build window, and the binary inherits its half-written tree;
  - run `git log -3` and `git status` before every staging;
  - commit fast. Long-lived dirty state is what another session sweeps up with `add -A`.
