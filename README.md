# Baton run state (BIM Agentic OS)

This orphan branch holds the relay coordination state, not product code.

- `LOCK.json` — the relay lock. Acquire by committing `{"state":"TAKEN",...}` and pushing; a rejected (non-fast-forward) push means another relay won: fetch, re-read, back off. Release by committing `{"state":"FREE"}`. A TAKEN lock with `last_heartbeat` older than 45 min may be taken over (log it).
- `journal.md` — append-only relay log.
- `OWNER.md` — the owner's inbox, decisions and standing rules (authoritative).
- `OWNER-TODO.md` — only what the owner can do (Revit runs, Load Once, approvals).
- `STATUS.md` — five-line snapshot.

Moved here from the owner's computer on 2026-11-02 (the old run is `.bim/baton/2026-10-23-core-ifc` there).
