# Wave <name> — common rules (relay N, <date>)

BASE: `<work branch>` = `<sha>`. Integration: only the controller merges.

## Protocol
- Work ONLY in your worktree (absolute path in your brief). Don't touch the work branch, the main checkout or other
  worktrees. Never search from the filesystem root (`find /`).
- **Before handing back, commit everything; leave no uncommitted changes or stray new files.** Commits: <house
  style>. Push your branch.
- Forbidden: `git push --force`, deleting remote branches, `git add -A`, `pkill -f`, `while pgrep -f …`.
- Don't edit `tasks.md`, the journal or `OWNER.md`: report, and the controller records it.
- <Scarce profile> **DO NOT BUILD:** don't build the app, run UI tests or boot simulators. Write the code carefully
  (read the real types and signatures before using them; no invented APIs), write its tests without running them,
  and commit. Cheap exception: <tests that don't spin up the heavy build>.
- <Ample profile> Verify with `<task oracle> <your tests>` before handing back.
- New strings for the catalog: only a snapshot in `<path>/<task>.json`; the controller applies them when integrating.
- In your report, list what the controller has to look at in the single build: new tests, compile risks, screenshots
  to take.

## File owners in this wave

| Task | Worktree | Owns |
|---|---|---|
| <T1> | <prefix>-<t1> | <files or areas> |
| <T2> | <prefix>-<t2> | <files or areas> |

If you need a file from another row, don't touch it: note it in your report. Hot files (<list>) have a single owner
per wave.
