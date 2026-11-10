# Relay181: doc-update dependents probe, owner save_as steps, drift adopt commit

Cloud scheduled relay, started 2026-11-10 15:53Z (LOCK was FREE after relay178). GitHub CI was the only oracle (no local .NET SDK or pwsh). Oldest-touched phases with ready work: 4 and 1 (relay174), 2 (relay176). Workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (0 blockers, 9 should-fix), every should-fix and the cheap NITs closed with tests, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P4-10 (Phase 4): `doc-update ... --probe-dependents <pipe> [--accept uid]...` — DocUpdateDependents.Plan over the Revit pipe, element.delete dry runs only, lazy single policy read, canonical plan; typed probe-mode envelope {code,key,operation_id,field,detail}, exit 1, no file; --accept bound 10000; plain doc-update bytes unchanged.
- P1-14 (Phase 1): -Catalogue copia group gains 5 document.save_as steps (outside-root refusal into a missing owner subfolder, save as -guardado.rvt with hash/path readback, target_exists repeat, readback on new file, document_conflict on the previous one); Test-CatalogueSaveTarget send guard; written-elsewhere stop; owner doc updated (copia 10/10, total 109).
- P2-12 (Phase 2): MirrorDriftAdoptCommit.Commit — P2-09 adopt ops committed as one ProjectStore revision with a deterministic key; committed / replayed (incl. racing duplicate) / nothing_to_adopt; typed drift_adopt_* refusals before any write. No provenance (P2-11 still waits for ADR0010).
- Board: P4-10, P1-14, P2-12 ✅; P4-11 ⏳ (deps done); new P1-16, P1-17, P1-18⛔, P2-13, P2-14⛔, P2-15⛔. Progress 126/148 (85.1 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P4-10 tests/cli: red 42850465603, green 42851792519; review red 42859559692, green 42860615743. 0 failed attempts.
- P1-14 tests/integration (fake + real CLI, Windows real save_as) + tests/cli catalogue-report e2e: red 42850218024, green 42853259446; review red 42863458581, green 42864247011. 0 failed attempts.
- P2-12 tests/mirror-journal (real ProjectStore temp dirs, byte/mtime no-write checks, racing duplicate via test hook): red 42851415073, green 42853024289; review red 42867411286, green 42869190717. 0 failed attempts.
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03 (ADR0011) wait for the owner; P1-14 owner run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0011 (recommended A); ADR0010 (recommended A); list one owner project folder outside .bim/native-runs in .bim/revit-enabled-roots.txt so c-as-outside is not blocked; still open — escritorio/estudio/biblioteca class; Uniclass excerpt licence.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P4-10: successful real-named-pipe probe exercised in-process only; owner step is P4-11.
- P1-14: Linux real CLI cannot save_as (path_relative), Windows is the authority; run record has no save_as_path yet (P1-18).
- P2-12: library only (P2-13 CLI, P2-15 MCP); map_adds/removals and restore plan not applied; racing-replay detection pinned to ProjectStore.Commit's return shape.
- Stray remote branch relay181-P4-10b (same commit as relay181-P4-10) could not be deleted from the cloud (HTTP 403 on delete); safe to delete.
ARCHIVOS CAMBIADOS: interfaces/cli/{DocUpdateCommand,DocUpdateProbe,Program}.cs, tests/cli/{DocUpdateProbeTests,Program}.cs, tests/cli/Bim.Cli.Tests.csproj, tests/cli/catalogue-report-record.ps1, scripts/{owner-catalogue-steps,owner-native-session}.ps1, tests/integration/{test-catalogue-session,fake-revit-command-executor}.ps1, docs/owner-verification-session.md, core/Bim.Core/MirrorDriftAdopt.cs, tests/mirror-journal/{MirrorDriftAdoptChecks,Program}.cs; board/features/contracts/attempts/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); dotnet run --project tests/cli; dotnet run --project tests/mirror-journal; pwsh -File tests/integration/test-catalogue-session.ps1.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P5-12/P5-13, P6-12, P7-11/P7-12 (relay176); then P3-10, P10-12/P10-13 (relay178); then P4-11, P1-16/P1-17, P2-13 (relay181). Owner: accept ADR0010/ADR0011; one -Catalogue run.
```
