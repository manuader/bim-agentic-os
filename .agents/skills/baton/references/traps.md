# Traps already paid for

Each of these cost hours on a real project with more than 90 relays. Format: symptom → cause → fix.

## Sessions and waking up

- **The closing relay schedules a cron and calls `clear_session`, and nobody wakes up** → the session cron doesn't
  survive the clear (5 of 5 tries were woken by the owner by hand) → an hourly routine with a LOCK gate. The closing
  relay only releases.
- **The closing relay calls `run_scheduled_task` to chain the next one, and nothing happens** → the hook blocks it in
  unattended sessions, and a routine with a run in progress can't be relaunched → don't chain. The hour is the clock.
- **A session was deleted with agents in flight, and their work was lost** → `clear`, `delete`, `archive` or `stop`
  with live subagents → forbidden. The close waits for "in flight: 0", even past 300k.
- **Another relay took a live session's LOCK** → the session was waiting on subagents and showed
  `isRunning: false` → a background heartbeat every 10 min. A session counts as dead only with both the heartbeat
  **and** `lastActivityAt` 45 min old or more.
- **The owner can't tell which of the routine's chats is the working one** → the waiting firings only said "someone
  else has the lock" → the closed-gate message with the link, plus session titles (waiting, ACTIVE, closed).
- **The LOCK stores an id the app doesn't know** → the relay wrote a made-up id, or one from another tool →
  `session:` is always the `sessionId` from `get_session("self")`.
- **The routine needs Computer Use, or an external write, and gets blocked** → a scheduled run can't approve them, and
  the classifier treats an order that comes from a file as injection → it goes to the owner's to-do, and gets done in
  a live session with the owner.
- **A "just in case" `TaskStop` killed an agent that was working** → it was used to hurry the close → only for an
  agent that already delivered and left a hung process, and it gets logged.

## Files and state

- **An owner request vanished between relays** → it was in the handoff, which is rewritten at every close → it
  belongs in `OWNER.md`, which only grows.
- **The owner answered the same question twice** → the answer stayed in a relay's chat → "Live answers from the
  owner" in `OWNER.md`, with the time and exact words.
- **Two tasks with the same number ("B13")** → the owner's session and the relay added tasks at the same time → read
  the spec and `OWNER.md` before numbering.
- **The journal grew past 600 KB** → nobody compacted it → compact it per `avo-harness`. Discarded directions and the
  current state survive; old narrative gets summarized into a side file.

## Git and worktrees

- **The agent worked on a stale base** → `Agent(isolation: "worktree")` branches from the main checkout's HEAD, not
  from the work branch → create the worktree by hand from the work branch and pass the absolute path.
- **A subagent can't use git in its worktree** → subagents launched from an isolated session get pinned to the
  launcher's worktree → launch them from a session in the main checkout, or let them write without committing and
  have the controller commit.
- **Someone else's commit swept up work in flight** → another session in the same checkout commits with `add -A` →
  explicit paths, commit fast, and `git log -3` + `git status` before every staging.
- **The binary did "impossible" things given the code you read** → the build inherited another session's
  half-written tree → every build in an isolated worktree, even if the tree looks clean.
- **The agent's cwd silently went back to the main checkout** → runs kept going, against the wrong tree → absolute
  paths and absolute build directories. Check `pwd` before each batch.
- **The disk filled up** → 41 worktrees took 47 GB → remove each worktree as soon as its task is integrated, and
  sweep at close with `clean-worktrees.sh`.
- **Moving a worktree broke the build** (a compiler crash, "defined in both") → build caches keep stale absolute
  paths → clear the moved package's caches.
- **The classifier blocked `worktree remove --force` + `branch -D`** → it treats them as irreversible destruction →
  `remove` without `--force` and `branch -d`, only for integrated work or work already on the remote. Everything
  else goes to the owner's to-do.

## Machine and tests

- **Agents spent 2 h waiting on builds, at load ~1000** → every task built and ran tests on a 16 GB machine → the
  scarce profile: agents don't build, and the controller builds the wave once.
- **A full oracle run took 20 h, and its result was worthless** → it ran alongside another session's recordings → one
  heavy thing at a time, `uptime` first, and the owner's session launches nothing heavy.
- **14 UI reds that "were a regression"** → machine load. All 14 passed in isolation → re-run isolated, on an idle
  machine, before bisecting.
- **A test failed on taps that weren't the test's** → the owner was using the same simulator → relay-owned
  simulators, never shared with live sessions.
- **A UI test run never finishes** → some runners hang after "Test run with…" → every background test gets a time
  limit. Once it expires, kill it by PID and count it as red.
- **`timeout: command not found`** → macOS doesn't ship it → use the tool's own time limit, or wait on the PID.
- **`pkill -f` killed another session's processes** → the generic pattern (`xcodebuild`, `recordVideo`) matched
  theirs too → kill your own PIDs only.
