# Relay185: deterministic fake pipe, mirror-drift-adopt CLI, DXF to ranked layouts end to end

Cloud scheduled relay, started 2026-11-10 23:54Z (LOCK was FREE after relay184). GitHub CI was the only oracle (no local .NET SDK or pwsh). Oldest-touched phases with ready work: 1 and 2 (relay181), then 6 (relay183). Workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (0 blockers, 9 should-fix), every finding closed with tests, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P1-19 (Phase 1): the fake revit-command pipe server keeps a listening instance across connections (next instance created right after each accept, max 2), fixing the intermittent Linux `revit-command --all` pipe_io_error that hit several relays' CI; 200-run --all stress loop on Linux and Windows. Production client unchanged (not a client defect).
- P2-13 (Phase 2): `mirror-drift-adopt store-directory selection.json` CLI verb over MirrorDriftAdoptCommit.Commit: strict canonical UTF-8 selection, typed drift_adopt_* refusals with the store byte-identical, committed/replayed/nothing_to_adopt.
- P6-13 (Phase 6): DXF → cad-brief → cad-plan-brief (site) → plan-layouts through the real CLI is byte-equal to the in-process pipeline; pinned digest identical on ubuntu/windows/macos; culture checks required on Linux CI.
- Board: P1-19, P2-13, P6-13 ✅; P2-14, P2-15 ⏳ (deps done); new P6-14 (cad-plan-brief --brief-only), P6-15⛔ (PDF to ranked layouts). Progress 135/154 (87.7 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P1-19 tests/integration catalogue-session (fake pipe + real CLI): red 42927364048, green 42930825232; review green 42935480549. 1 failed attempt (stress budget).
- P2-13 tests/cli: red 42927683523, green 42930250939; review red 42933487880, green 42933556749. 0 failed attempts.
- P6-13 tests/cli: red 42927416675, green 42929293306; review green 42937560412. 0 failed attempts.
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; P4-11 / P1-14 owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: P1-19 stress: 200 paged --all runs ≈ 37 s per core job; before the fix 1/50 runs failed on Linux.
GAPS CONOCIDOS:
- P1-19: the real add-in pipe server (runtimes/revit-native, Windows) not examined for the same pattern.
- P2-13: engine is CLI-internal (P2-15 moves it to interfaces/mcp); not chained with real mirror-drift/mirror-drift-select binaries (P2-14); internal_error from the catch-all gives no nothing-written guarantee.
- P6-13: plan-layouts takes only a bare brief, so the chain copies the `brief` member between verbs (P6-14); child culture not directly probed.
ARCHIVOS CAMBIADOS: tests/integration/{fake-revit-command-pipe,test-catalogue-session}.ps1; interfaces/cli/{MirrorDriftAdoptCommand,Program}.cs, tests/cli/{MirrorDriftAdoptTests,CadToLayoutsTests,Program}.cs; board/features/contracts/attempts/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P5-12, P7-12/P7-13 (relay183); then P3-11, P4-12/P4-13, P10-13 (relay184); then P1-16/P1-17/P1-18, P2-14/P2-15, P6-14 (relay185; P6-14 and P7-13 both near plan-layouts — P6-14 only touches CadPlanBriefCommand.cs). Owner: ADR0010/ADR0011; one -Catalogue run.
```
