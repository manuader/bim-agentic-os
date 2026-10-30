# Relay68 — owner scope change to a general Revit command surface

The owner ran the first owner-attended native session (20261030T192810Z on 0641412). check-native-source passed, the Release builds passed, and N21 runs 1–2 passed the full A/B 52/52; run 3 timed out waiting for Load Once. The owner then changed the product goal: Claude should be able to make Revit do any request. Decisions are in [ADR0009](architecture/adr/0009-revit-command-surface.md): catalogue plus a guarded free path, test copies only, architecture first, and build-only Revit reference assemblies in CI.

## Landed on main

- **C01.** CI `native-build` compiles the add-in on windows-latest against Nice3point.Revit.Api 2025.0.2, CI only. Local builds without Revit fail.
- **C02.** Command protocol and catalogue v1 contracts: queries, wall line/arc, door/window, floor, room. Includes the copies-only guard (test roots plus the owner file `.bim/revit-enabled-roots.txt`; links and hard links refused), receipt verification, the CLI `revit-command` verb and generated MCP tools. Review found 10 issues, all fixed with tests.
- **C03.** Executor in the add-in: one transaction group per write, readback, rollback, ExtensibleStorage receipts. Review found that the pipe opened in any Revit and that legacy writes skipped the guard; the pipe now opens only in validated fixture processes, and legacy writes share the guard.
- **C04 prepared.** `scripts/owner-native-session.ps1 -Catalogue` runs 17 steps in a new fixture: queries, a 6×4 m wall rectangle, a curved wall, a door, a window, a floor, room "Prueba", replay, conflict and an invalid arc. It was tested headless against fake pipes in CI.

## Limits

Nothing from C02–C04 has run in Revit yet. The relays scheduled between 2026-10-24 and 2026-10-30 landed no work; their prompt was rewritten with the ADR0009 roadmap and a rule to always release the LOCK.
