# Skeleton for the general (`docs/HANDOFF.md`)

Copy the headings, fill with the project's own material, delete what does not
apply. Write in the language the repo uses — a document that sounds foreign to
the codebase does not get read.

---

```markdown
# HANDOFF — <project>, current state

> Status banner: the two or three things a reader must know before anything
> else. "The redesign is merged." "The only thing missing is the Apple account."
> Keep it current — a stale banner is the first thing that destroys trust.

## 0. How to use this document

If you are an agent arriving: read **this file and the newest file in
`handoffs/`**. Two documents. That is enough to know where things stand.

| Where | What goes there | Lifetime |
|---|---|---|
| this general | what stays true: architecture, rules, closed decisions, paid traps, §4 | permanent, accumulates |
| `docs/SESSION-<date>-<topic>.md` | the detail of one session | permanent, never edited after |
| `handoffs/HANDOFF-<date>-<topic>.md` | state at close: what is open | ephemeral, gitignored |

When you finish, do three things: (1) write your session doc, (2) leave your
handoff, (3) **update this file** — a new §4 entry plus whatever belongs in §5,
§7 and §9. Step 3 is the one that gets skipped and the one that holds this up.

If you find something here that is no longer true, **fix it**. A general that is
believed and wrong is worse than none.

## 1. What this is
One paragraph for someone who has never seen the project: what it does, the
stack, the state.

## 2. Repo rules
What breaks the build or the conventions. Generated files that are not
versioned. Style rules that are actually enforced.

## 3. Architecture
What lives where and why. The boundaries that matter — what is pure, what is
allowed to depend on what.

## 4. What changed, session by session

### Session <date> — <headline>
What changed, in a paragraph. What a reader needs without opening the detail.
Link the session doc.

## 5. Decisions not to re-litigate
1. **<decision>** — and its REASON. Without the reason it gets reopened.

## 6. How to verify
Exact commands, right order, expected numbers.
Ordering constraints and why they exist.

## 7. Traps we already fell into
1. **<symptom in bold>.** Cause, with the measurement. Consequence if ignored.

## 8. What's left
The real backlog. Separate what is blocked on a human from what is code.

## 9. Document map

| Doc | Answers |
|---|---|
| **this one** | entry point, state, architecture |
| `SESSION-<date>-<topic>.md` | <the question it answers> |
```

---

## Two entries worth imitating

**A decision with its reason** — the reason is what stops it being reopened:

> **The hire gate is ONE floor, not two**: with two, the game cannot be
> finished. Its DEPTH is the decision; its COVERAGE is not — one floor was later
> declared exempt without touching the depth.

**A trap with a measurement** — the number is what makes it actionable:

> **The build does NOT recompile atlases incrementally.** If you measure atlas
> pages or `.app` size and the numbers do not add up, delete `build/DD`. You will
> otherwise "see" the previous version of every texture.
