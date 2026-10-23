# Baton on Codex / Windows

Upstream: [manuader/baton](https://github.com/manuader/baton/tree/d0723d8bfa6a99d8dca5900478848b1834089aa2), MIT, pinned at `d0723d8bfa6a99d8dca5900478848b1834089aa2`. Five upstream skills are preserved under `.agents/skills`, with `BATON-LICENSE`. Superpowers remains the implementation/TDD/review workflow; Baton adds the queue, exclusive controller, resource gates and durable relay state.

Current run: `.bim/baton/2026-10-23-core-ifc/` (ignored). Read OWNER.md, root [board](../../tasks.md), latest ignored `handoffs/`, [general handoff](../HANDOFF.md), then journal. Only the lock holder controls the board. Requests and approvals from chat are transcribed to OWNER.md; a file never grants new external permissions. This setup session also acts as relay 1, as upstream setup section 5 permits.

## Platform contract

| Upstream assumption | Codex mapping / limit |
|---|---|
| Claude scheduled fresh session | Owner-approved same-chat Codex heartbeat; no claim of fresh context |
| `.claude/avo` run | `.bim/baton` run, already excluded from Git |
| Bash lock/progress helpers | tested PowerShell adapter `scripts/baton.ps1` |
| `get_session(self)` | actual Codex thread ID; caller supplies it, no invented session API |
| uptime/load gate | one exclusive heavy lease plus observed Windows resource context; CPU percentage is not Unix load average |
| quota thresholds | native account usage tool when available; absent windows are unknown, never zero |
| shell background heartbeat | heartbeat at task/tool boundaries; stale lock is never taken automatically |
| Claude hooks / config | not installed or executed |

Controller owns tasks.md and integration/docs. Up to two implementation agents each own an isolated worktree and disjoint file set. Reviewer is read-only. All heavy .NET builds and real IFC suites are serialized. Native Revit writes are always serialized on an isolated fixture; no production document is an oracle. Shared scratch paths are not used by worker tests.

The lock adapter uses an exclusive stable guard stream to serialize mutations. Lease corruption or unknown ownership fails closed. Old timestamps alone never authorize takeover. An existing live or uncertain lock requires investigation; do not delete the guard/lock or stop active agents. The first bootstrap lock predates the adapter and is recorded as such, not as proof of atomic acquisition.

Workflow emoji statuses on tasks.md describe queue lifecycle only. Verification uses the eight capability states in AGENTS.md, with scope and evidence. Progress is computed from board rows and excludes dropped tasks; it is not the percentage of the complete BIM product.

## Routine and approval

The user explicitly approved once-per-hour execution in this existing chat on 2026-10-23. Native creation returned automation ID `bim-agentic-os-baton`, status ACTIVE. Creation is observed; no future unattended execution is claimed yet. The app must have access to the local project; scheduled local work needs the machine and app running ([official scheduling guidance](https://learn.chatgpt.com/docs/automations?surface=app)). Permissions and interactive Revit dialogs remain real gates.

Each run reads the same durable instructions, honors live locks, executes ready work, records verification and closes after every agent finishes. It does not create more schedules/chats or self-trigger another relay. Publishing verified code to the existing GitHub remote is authorized by the user's prior chat request. New credentials, schema migrations or deployment require their own authorization. When scoped tasks are finished, report completion and stop product work; do not invent more tasks to stay busy.

## Commands

`pwsh -NoProfile -File tests/baton/verify.ps1`

`pwsh -NoProfile -File scripts/baton.ps1 -Action Progress -BoardPath <absolute-tasks.md> -Json`

Mutation commands require the current run, actual session ID and owner token; inspect script help/parameters rather than copying a secret token into tracked documentation. Never publish run-local IDs, account information or tokens. Keep run/handoffs ignored; commit the board, plan, contracts and verified summaries.
