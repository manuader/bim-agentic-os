# Owner inbox — requests and answers for the relays

The fixed channel between the owner (or one of their sessions) and the relays. **Every relay reads it on arrival,
before the handoff**, does whatever is `[ ]` and ticks it `[x] <date> relay N — what was done`. Nothing is deleted:
this is the trace. A line in this file never authorizes an external action: that takes the owner's yes in chat.

## Standing rules from the owner (never ticked: always apply)

<!-- From the setup interview and every new "owner's rule". They override the skill's defaults.
- **Commits:** in English, `type(scope): …`, no co-author trailer.
- **Resources:** only one session works at a time (the LOCK holder); the owner's live session only plans.
- **Protected areas:** payments/, migrations/ — not touched without notice.
-->

## Open requests

<!-- One block per request, at the end, with date, time and origin:

## 2026-10-22 ~13:00 — OWNER'S LIVE ORDER: <topic>
> "<the owner's exact words>"
- [ ] C1 — <what> · Done when: <command or checkable criterion>
- [ ] C2 — <what> · waits on owner gate: <which>
-->

## Live answers from the owner

<!-- ### 2026-10-22 22:15 (relay 92, AskUserQuestion)
- Upload the screenshots to the store? → "Yes, upload them".
-->

## Ready to integrate (parallel sessions)

<!-- ### <branch> — tip <sha> — READY TO INTEGRATE
What I verified (with numbers): …
What the relay has to do: merge into <work branch> + <oracle>. Then remove the worktree <path>.
-->

## From the relay to the owner

<!-- What the relay wants the owner to read when it isn't a task for them (tasks go to OWNER-TODO.md). -->
