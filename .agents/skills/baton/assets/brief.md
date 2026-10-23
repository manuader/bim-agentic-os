# Brief — <task>: <short title>

Worktree: `<absolute path>` · branch `<prefix>/<task>` (base `<sha>`, tip of `<work branch>`).
Common rules (read them first): `<run>/waves/<wave>/common.md`.

## What
Spec: `<plan file>`, section "<task>". Its "Done when" rules; the decisions above it are not re-asked.
- <what isn't obvious: the current state, where to look, what already exists and gets reused>
- <the known trap this task touches, if any>

## Owns
See the table in `common.md` (row <task>).

## Verification
- <Scarce profile> DO NOT build or run the app's tests. Write the "Done when" tests without running them.
- <Ample profile> `<task oracle> <classes>` green before handing back.
- Commit(s) in the house style, and push.

## Checkpoints (only if it produces something the owner approves)
Stop at <script | first render | master>: send the path to the controller with SendMessage and wait for its answer.

## Report
What changed (files), tests written, risks, what the controller must run or capture, final hash.
Status: DONE / DONE_WITH_CONCERNS / NEEDS_CONTEXT / BLOCKED.
