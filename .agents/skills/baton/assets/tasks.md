# <Project> — task board

> The single board of the plan: what is done, what comes next, what waits on whom.
> **One writer: the controller** (the relay holding the LOCK). It updates the board at every task boundary: on
> dispatch, on hand-back, on integration and at wave close. Subagents never edit it: they report, and the
> controller records it. The owner's sessions don't edit it either: requests go to `OWNER.md`, and the relay adds
> the rows.
> Source plan: `<docs/PLAN.md>`. Progress is computed, never typed: `bash <skill>/scripts/progress.sh tasks.md --update`.

## Statuses

| Mark | Meaning | Counts as |
|---|---|---|
| ✅ | done: integrated into the work branch and verified by the oracle | done |
| 🟢 | coded and reviewed, waiting for the wave's build or for integration | open |
| 🔧 | being fixed: red in review, in the build or after integration | open |
| 🔄 | in flight: an agent is on it (Notes: agent and worktree) | open |
| ⏳ | ready to dispatch: every dependency is ✅ | open |
| ⛔ | blocked by a dependency (Deps says which) | open |
| 🔒 | waiting on the owner (the item is in `OWNER-TODO.md`) | open |
| ⏭️ | dropped by the owner's decision (Notes: why and when) | left out of the total |

Rules:

- A task goes from ⛔ to ⏳ when all its dependencies are ✅ and no agent in flight owns its files.
- ✅ needs the commit hash in Notes.
- A task is never deleted. Only the owner drops it, with ⏭️.

## Progress

<!-- computed by progress.sh — do not edit by hand -->
Progress: 0/3 done (0.0 %) · 🔄 0 in flight · 🟢 0 coded · 🔧 0 fixing · ⏳ 1 ready · ⛔ 2 blocked · 🔒 0 owner · ⏭️ 0 dropped

## Tasks

### <Phase 1 — name>

| ID | Task | Status | Deps | Owns | Done when | Notes |
|---|---|---|---|---|---|---|
| P1-T1 | <what, in one line> | ⏳ | — | `<files or area>` | `<command>` exits 0 | |
| P1-T2 | <what> | ⛔ | P1-T1 | `<files>` | <checkable criterion> | |

### <Phase 2 — name>

| ID | Task | Status | Deps | Owns | Done when | Notes |
|---|---|---|---|---|---|---|
| P2-T1 | <what> | ⛔ | P1-T2 | `<files>` | <checkable criterion> | |

## Owner requests added by relays

<!-- Rows for the [ ] items of OWNER.md, with an ID that doesn't collide (the relay reads the board and OWNER.md before numbering). -->

| ID | Task | Status | Deps | Owns | Done when | Notes |
|---|---|---|---|---|---|---|
