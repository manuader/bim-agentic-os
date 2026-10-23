# Baton relay 1 — compiler-driven IFC slice

Authority: existing master specification and [cycle scope](../agent/CYCLE_SCOPE.md). This executes the next pending slice; it does not reopen product discovery or alter saved BIM-IR records.

## Hypothesis and oracle

The existing C# RoomCompiler and incremental diff can drive the tested IFC boundary through an adapter-only request carrying exact serialized snapshots, their hashes, stable logical roles and the compiler plan. A custom initial width must work, preventing a fixed-fixture implementation from masquerading as compiler execution. Native documentation stays unsupported until independently implemented and checked in Revit.

Variants considered: Python reimplementation of the compiler (duplicates authority); migration of BIM-IR (unapproved and unnecessary); adapter envelope over existing records (selected smallest executable boundary). Verify tamper rejection before any output write, custom-width create/resize, physical readback, stable identities, retry and rollback. Preserve existing strict A/B acceptance.

## Tasks and ownership

| ID | Dependencies | Owner/files | Done when |
|---|---|---|---|
| B01 | none | baton_adapter: scripts/baton.ps1, tests/baton | PowerShell oracle proves concurrent exclusive acquisition, ownership, corruption rejection and computed progress |
| B02 | none | controller: installed skills, docs/harness, tasks.md, run files | pinned MIT source, truthful Codex mapping, owner approval and actual heartbeat result recorded |
| I01 | none | compiler_ifc: adapters/ifc, tests/ifc-compiler, CLI, IFC worker/tests, evaluation/IfcExecution.cs | TDD then real compiler packet drives create/resize and rejects invalid packets before writes |
| I02 | B01, I01 | controller: CI/verification wiring, evidence and contracts | independent reviews resolved; fresh complete gate and IFC integration pass; verified changes published |
| N01 | I02 | future isolated native task | native hosted elements, Room and affected documentation pass full Revit A/B; remains pending here |

Controller integrates and documents only. Two implementation worktrees, one owner per file, no Revit document mutation in this wave. One heavy build/test window at a time. Tests have at most three failing attempts each; record hypothesis/change/result and stop the affected test at exhaustion. No blanket schema, RPC or dependency changes.

## Review, publication and close

Review coordination adapter and compiler boundary independently. Add deterministic tests and CI integration without relaxing existing criteria. Publish hashes/counts/benchmarks, not local binaries or RVTs. Distinguish IFC backend acceptance from product/native acceptance. Close only with no agents in flight, computed board, state/blockers/attempts, session/general/ephemeral handoffs and a safely released lock. Hourly Codex heartbeat is owner-approved; it uses this chat, not a fresh Claude session.
