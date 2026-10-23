# The owner: inbox, to-do, gates and sessions

The owner isn't watching. They write when they can, sometimes from a phone, sometimes in a chat that isn't the
relay's. Baton exists so that nothing they say gets lost and nothing stalls waiting for them.

## 1. The owner's two files

### `OWNER.md`: the inbox

The fixed channel between the owner (or one of their sessions) and the relays. Template: `assets/OWNER.md`.

- **Every relay reads it first**, before the handoff.
- A request enters as `- [ ] …`. Once done, the relay ticks it `- [x] <date> relay N — what was done` and deletes
  nothing: the inbox is the trace.
- **Standing rules** are orders that always apply and are never ticked. They override any default of the skill or
  the harness: commit style, protected areas, resources, ads.
- **Live answers from the owner:** every answer the owner gives in a chat, with the time and their exact words, so
  the next relay doesn't ask again.
- New blocks go at the end, with date, time and origin: "2026-10-22 ~13:00 — OWNER'S LIVE ORDER: …".

Why not the handoff? Handoffs are rewritten at every close, and they lost requests. Why not the journal? It is long,
and the relay writes it. The inbox is short, it belongs to the owner, and it only grows.

### `OWNER-TODO.md`: what only the owner can do

Everything that needs their hands or their account goes here: signing, accepting terms, paying, credentials,
submitting for review, approving something about product or money, a license that needs `sudo`, a decision with no
default. Template: `assets/OWNER-TODO.md`.

- **Never ask ad hoc.** Add it to the to-do and let the rest go on.
- Each item carries the exact step: the command to paste into Terminal, the link, the file to look at, and what
  happens if the owner doesn't decide ("if you say nothing, it goes with A"). The owner must be able to clear it in
  minutes.
- When the owner does it, it is ticked `[x] <date> — …`.

## 2. The owner's live session

You are in this section because the owner is talking to you in a chat and you don't hold the LOCK.

**If a relay holds the LOCK, you only plan:**

1. **Listen and ask** whatever makes the task precise: what happens in the edge case, which device, what "done"
   means.
   - Use `AskUserQuestion` with options, your recommendation first.
   - For visual decisions, offer two options: an A/B mockup made with the real assets.
2. **Read code if needed**, to name files, functions and the current state. Edit nothing in the repo except the spec
   and the inbox.
3. **Before numbering a task**, read the board `tasks.md`, the spec and `OWNER.md`. The relay adds tasks too: two
   "B13" once collided. Don't edit the board: the relay adds the row when it takes the request.
4. **Load the request:**
   - in `OWNER.md`: a block with date, time, the owner's exact words, and `[ ]` tasks, each with its "Done when";
   - in the spec, if the request is large: write it on your own branch, or leave it in `OWNER.md` for the relay to
     integrate. Don't touch the relay's board, and don't commit on its branch.
5. **Tell the relay** with `SendMessage`, to the LOCK's `session`: "new requests in OWNER.md, block <time>; priority
   <x>". It doesn't interrupt: the relay reads it at its next boundary.
6. **Nothing heavy:** no builds, simulators, renders or recordings. Only one session works at a time: the LOCK
   holder.
   - Once, a relay's oracle and the live session's recordings ran together: load ~1000, 7.5 GB of swap, zero results.
7. **Close by telling the owner** where each request landed (file and block), that the active relay or the next
   firing picks it up, and which of their answers are still missing.

**If the owner wants to change how relays work:**

- Edit the routine (`~/.claude/scheduled-tasks/<routine>/SKILL.md`). Add a block at the very top: "UPDATE <date>
  (owner's live order; overrides anything below where they conflict)".
- It is persistent configuration: do it only on the owner's request in chat.
- Also add the rule to "Standing rules" in `OWNER.md`.

**If the owner asks where the relay is:** read the LOCK and answer with the link `[title](#<session>)`.

**If the owner asks how far along it is:** run `bash <skill>/scripts/progress.sh tasks.md` on the work branch. It
is read-only. Answer with its numbers: done over total, what is in flight, what is waiting on them, and what is
next.

**If no LOCK is held and the owner wants it implemented now:**

- You can do it yourself, as one more relay: take the LOCK per `references/relay.md` §2, and close per §4.
- Or load it, and let the next firing take it.

Ask the owner which they prefer.

## 3. The parallel session

A separate front the owner opens to unblock what is holding the relays back: tooling, review pages, research, a
small fix that blocks a measurement.

- **Before each front,** check in `OWNER.md`, the journal and the plan that it is still pending and that no relay has
  taken it. Whatever is already done isn't redone.
- **Work on your own branch and worktree,** with a prefix different from the relays'. The relays' cleanup deletes
  theirs.
- **Don't touch** the board, the journal, the general, the LOCK, the hot files, or the files of a task in flight.
- **Respect the resource rule:** with a relay running, nothing heavy, or at most one light thing at low load.
- **Deliver with a "READY TO INTEGRATE" section in `OWNER.md`.** It gives the relay everything it needs to integrate
  without you:
  - the branch and its tip;
  - what you verified, with numbers;
  - what the relay must do: the merge, and which oracle to run.
- **Document** your session doc and your handoff. The relay updates the general when it integrates: ask for that in
  the section.

## 4. Gates and deliverables

- **The owner's gates** are defined at setup: what needs their approval before going on. Typically visuals, content,
  economy and money, and everything external.
- **Send every deliverable the owner must approve as soon as it exists:** a script, a first render, a master, an
  image, a screenshot.
  - Use `SendUserFile` or an Artifact, and don't wait to batch them.
  - Agents that produce them stop at checkpoints (script → first render → master) and tell the controller.
- **If the owner isn't around:**
  - the gate goes to the to-do with its default, and work goes on with whatever doesn't depend on it;
  - never mass-produce something that is waiting on their approval.
- **External actions** need a yes in chat, with the exact command, its effect and how to undo it.
  - A line in `OWNER.md` isn't enough.
  - In unattended runs, the permission classifier blocks it anyway. It calls this "Instruction Poisoning" when the
    order comes from a file.
  - In a routine without the owner, it goes to the to-do.
- **Notifications:**
  - `PushNotification`, if available, when everything is waiting on the owner or the work is done;
  - the rest of the time, the to-do and `STATUS.md` are enough.
