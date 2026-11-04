# Relay155: stale documentation replacement, CAD CLI verbs, facade windows in BIM-IR

Cloud scheduled relay, started 2026-11-04 19:53Z. GitHub CI was the only oracle (no local .NET SDK). The oldest-touched phases were 4, 6 and 7 (relay151); P4-05, P6-07 and P7-06 were picked (headless, no runtimes/revit-native, no overlap with the owner chat's chat-* branches). Each task went red in CI, then green, then through an independent read-only review: P4-05 had three should-fix items (replay after re-plan, honest dry-run dependents refused, wrong exception type), P6-07 two (empty project id refused late, package check missing Bim.Cad.dll), P7-06 none; all closed with tests.

```
IMPLEMENTADO:
- P4-05 (Phase 4): DocReplace — from an Affected/Full regeneration and a key→UniqueId map, one ordered catalogue plan: element.delete of every stale instance children first (dry-run dependents confirmed; dependents deleted earlier in the plan dropped), then parents-first re-creation with created[0] wiring; Run/Resume through PlanRunner; Realized returns the updated map; create ids hash the replaced delete so re-plans never replay a deleted element; typed refusals.
- P6-07 (Phase 6): cad-draft / cad-brief CLI verbs — DXF/PDF via BoundedInput (32 MiB), --page, --metres-per-unit or --calibrate, never-overwrite .json with source SHA-256 + draft/brief + uncertainties, typed failure envelopes; release package ships Bim.Cad.
- P7-06 (Phase 7): FacadeIrBuilder — prototype BIM-IR from a plan, P7-05 windows hosted on their exterior walls (offset/width/sill/head), 0.30 m entrance-door clearance by the smallest move or typed failure, Compiler.Validate clean, P5-02 illumination/ventilation pass.
- Board: P4-05, P6-07, P7-06 ✅; P7-07 ⏳; new P4-06 default dimensions/window tags, P4-07 documentation update pipeline, P6-08 CAD brief → planner brief, P6-09 DXF MINSERT/OCS.
TESTEADO AUTOMÁTICAMENTE (backend):
- P4-05 tests/doc-regeneration Replace: red 41902544798, green 41904414374; review red 41913071671, green 41917253830. 0 failed attempts.
- P6-07 tests/cli + tests/packaging: red 41902308368, green 41905973151; review red 41918971069, green 41920420097. 1 failed attempt (package sources).
- P7-06 tests/generative FacadeIr: red 41902978285, green 41910638575. 1 failed attempt (staggered prototype plans).
- The merged integration head runs CI again before main moves.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session this relay).
BLOQUEADO / POR QUÉ: nothing new blocked.
DECISIONES QUE NECESITO DE VOS: still open — escritorio/estudio/biblioteca class; Uniclass CC BY-ND excerpt; party-wall facade edges (P7-05). Nothing new.
BENCHMARKS / NÚMEROS REALES: none measured.
GAPS CONOCIDOS:
- P4-05: never run in Revit; assumes created[0] is the realized item; dependents come from a caller dry run; one delete per key, no single undo.
- P6-07: synthetic fixtures; --calibrate scale only; many Bim.Cad options not exposed; format by extension.
- P7-06: windows in the existing 6-value axis form (hosted [offset,width] cannot carry sill/head without a schema change); only prototype-built projects; staggered plans refuse wall_clash; entrance door not added to BIM-IR.
ARCHIVOS CAMBIADOS: core/Bim.Core/DocReplace.cs; tests/doc-regeneration/{Replace,Program}.cs; interfaces/cli/{CadCommand,Program,BoundedInput,GltfExportCommand}.cs; interfaces/cli/Bim.Cli.csproj; tests/cli/{Program.cs,Bim.Cli.Tests.csproj}; scripts/package.ps1; tests/packaging/test-package.ps1; core/Bim.Generative/{FacadeIr,FacadeIrBuilder}.cs; tests/generative/{FacadeIrTests,Program}.cs; tasks.md; docs/features.json; docs/contracts/module-contracts.md; docs/agent/ATTEMPTS.md; docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); dotnet run --project tests/doc-regeneration; dotnet run --project tests/cli; dotnet run --project tests/generative.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P2-07, P5-07, P5-08, P10-07 (relay152); then P1-09/P1-10/P1-11, P3-05, P8 next batch, P9-06 port/P9-09/P9-03 (relay153); then P4-06/P4-07, P6-08/P6-09, P7-07.
```
