# Relay162: documentation update pipeline, CAD brief to planner brief, staggered prototype snap

Cloud scheduled relay, started 2026-11-05 11:53Z. GitHub CI was the only oracle (no local .NET SDK). The oldest-touched phases were 4, 6 and 7 (relay159); P4-07, P6-08 and P7-08 were picked. Three workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (0 blockers, 11 should-fix), every should-fix closed with tests, integration head re-run in CI before main moved. Phases 4 and 6 had no ⏳ left, so their next batches were loaded (P4-08, P4-09, P6-10, P6-11).

```
IMPLEMENTADO:
- P4-07 (Phase 4): DocUpdate.Update/Plan/Run/Resume — one entry point from a BIM-IR edit to the documentation replacement: DocDefaults before/after → Affected + forced (changed/new definitions, never-realized items) + retired keys → DocReplace through PlanRunner; updated realized map; removed elements pruned; already-gone instances reported in Gone instead of failing.
- P6-08 (Phase 6): BriefPlanner.ToPlannerBrief — CAD brief draft → validated planner brief; low-confidence / area-mismatch / duplicate-label rooms dropped with typed reasons or kept when the caller confirms; adjacencies filtered; SpacePlanner plans the result.
- P7-08 (Phase 7): PrototypeSnap — opt-in pre-pass snapping jogs thinner than the wall onto a common line (lot line first), CABA checks or typed refusals; real prototypes 10/16 accepted (was 6), 0 wall_clash.
- Board: P4-07, P6-08, P7-08 ✅; new P4-08, P4-09, P6-10, P6-11 ⏳.
TESTEADO AUTOMÁTICAMENTE (backend):
- P4-07 tests/doc-regeneration: red 42023285220, green 42024173720; review red 42029523294, green 42030387084. 0 failed attempts.
- P6-08 tests/cad: red 42023139520, green 42024176366; review red 42033774848, green 42034941467. 0 failed attempts.
- P7-08 tests/generative: red 42024783818, green 42025388127; review red 42037618668, green 42038221886. 0 failed attempts.
- Merged integration head re-run in CI before main moved (see journal).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session this relay; all headless).
BLOQUEADO / POR QUÉ: nothing new blocked.
DECISIONES QUE NECESITO DE VOS: nothing new; still open — escritorio/estudio/biblioteca class; Uniclass excerpt licence; party-wall facade edges.
BENCHMARKS / NÚMEROS REALES: P7-08 on the 16 real P7-07 prototypes: 10 accepted, 6 duplicate_geometry, 0 wall_clash; 6 plans snapped, 16 edge moves, max 0.026611 m. P6-08 on brief-plan.dxf: 6/7 rooms kept (bano dropped area-mismatch + low-confidence; bano-2 promoted).
GAPS CONOCIDOS:
- P4-07: fake transport only; dependents strict (two-pass by hand → P4-09); no CLI (P4-08); missing UniqueId inside confirm_dependents still fails.
- P6-08: synthetic DXF only, no PDF path; no footprint from the drawing; no CLI (P6-10); all-non-ASCII labels not extracted (P6-11).
- P7-08: snaps onto existing lines only (no modular grid); snapped plans keep pre-snap score/details; not in CLI/MCP (P7-09).
ARCHIVOS CAMBIADOS: core/Bim.Core/DocUpdate.cs; adapters/cad/Bim.Cad/{BriefPlanner,BriefPlannerModel,BriefExtract}.cs; core/Bim.Generative/{PrototypeSnap,PrototypePipeline}.cs; tests/{doc-regeneration,cad,generative}; tests/cad/Bim.Cad.Tests.csproj; board/features/contracts/attempts/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); dotnet run --project tests/doc-regeneration; dotnet run --project tests/cad; dotnet run --project tests/generative.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P2-09, P5-09..P5-11, P10-08 (relay160); then P1-11, P3-07, P8-09, P9-06 port/P9-03 (relay161/157; check origin chat-* branches before any Revit command file); then P4-08/P4-09, P6-10/P6-11, P7-09/P7-10 (relay162).
```

Owner: no new Revit step from this relay (all headless).
