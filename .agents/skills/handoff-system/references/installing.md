# Installing the handoff system in a repo

A one-time setup. After this it maintains itself, because the protocol lives in
the general and the general is the first thing every agent reads.

## 1. Create the directories

```bash
mkdir -p docs handoffs
```

Match whatever the repo already uses (`Docs/`, `documentation/`) rather than
imposing a new convention.

## 2. Gitignore the ephemeral handoffs

```bash
echo "handoffs/" >> .gitignore
```

They are notes between agents, not history. Versioning them turns them into a
pile where nobody knows which one is current — and only the newest matters.

The session docs and the general **are** versioned: they are the record.

## 3. Build the general

Use the **writing-general-handoff** skill. Reconstruct from the tests, `git log`
and the code comments — not from the README, which is usually the oldest thing
in the repo.

## 4. Make it self-triggering

Add one line to the repo's agent instructions file (`CLAUDE.md`, `AGENTS.md`, or
equivalent):

```markdown
Before starting, read `docs/HANDOFF.md` and the newest file in `handoffs/`.
When you finish, write your session doc, leave your handoff, and update
`docs/HANDOFF.md`.
```

This is what makes the system run without anyone remembering it. Without it the
general is just another file nobody opens.

## 5. Verify it works

The test: an agent that reads **only** the general and the newest handoff should
be able to

- build the project,
- run the tests knowing which numbers to expect,
- say what must not be touched and why,
- say what comes next.

If any of those is missing, it did not get written. Go back and add it.

## Adapting to a repo that already has docs

Most repos have something. Do not start over:

- An architecture doc or ADRs → they become §3 and feed §5.
- A CHANGELOG → raw material for §4, but a changelog lists *what*; §4 needs
  *what a reader must know*.
- A stale README → treat every claim as a hypothesis and check it against the
  code before promoting it.

When a doc and the code disagree, the code wins and you fix the doc.
