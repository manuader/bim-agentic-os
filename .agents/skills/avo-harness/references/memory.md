# Persistent memory

The context runs out, gets compacted, and is lost between sessions. The journal isn't. The rule that orders
everything else: **if something must survive this context window, it goes to the journal the moment you know it**.
Not at the end, not "when I have a minute". The end arrives with the context already trimmed.

In AVO, persistent memory carried previous implementations, evaluation results, compiler and profiler output, and
the accumulated reasoning. That let each session resume from the current state instead of rebuilding the search
every time. Same here.

## Location

`.claude/avo/<YYYY-MM-DD>-<slug>/journal.md` inside the project. It is excluded from the repo through
`.git/info/exclude`, which is local and doesn't touch the team's `.gitignore`. If there's no repo, or the directory
isn't writable, use the session's scratchpad, and warn the user that this state dies with the session.

Large artifacts (logs, profiles, the diffs of a discarded variant) go in sibling files, referenced from the journal
by path. The journal is read in full every time, so keep it readable.

## Structure

Three blocks, in this order, because it's the order in which whoever resumes needs them:

| Block | What it holds | How it changes |
|---|---|---|
| **Charter** | goal in one sentence · oracle · budget · constraints | Written once. Edited only if the goal changes, and the change is noted. |
| **Current state** | where the oracle stands today · live hypothesis · what's committed · next step | Rewritten in place every iteration. |
| **Discarded** | each line: what was tried, what the evidence showed, why it was closed | Only grows, never deleted. |

The discarded block is the run's most valuable asset. It is what keeps the next session from repeating last week.

After those three blocks comes the iteration history, in chronological order.

## Resumption

```bash
ls -t .claude/avo/*/journal.md 2>/dev/null | head -5
```

With an open run:

1. Read the charter, then the current state, then the discarded list.
2. Run the oracle to see where you stand **today**. The tree may have changed since the last session.
3. Only then decide the next step.

Don't start editing before running the oracle. Resuming on an assumed state is the fastest way to add a new bug to
the one you were already chasing.

## Compaction

Past ~200 lines, compact:

- the charter, the current state and the discarded list stay intact;
- the iteration history gets summarized into one paragraph per closed direction.

What gets thrown away is the narrative. What gets kept is whatever narrows the search space.

Mark the compaction with a date, so it's visible that one happened:

```markdown
## Compacted 2026-09-06 — its 01-14 summarized above in Discarded
```
