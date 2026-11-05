# Relay159: default dimensions and window tags, DXF MINSERT/OCS, prototype pipeline

Cloud scheduled relay, started 2026-11-05 03:53Z. GitHub CI was the only oracle (no local .NET SDK). The oldest-touched phases were 4, 6 and 7 (relay155); P4-06, P6-09 and P7-07 were picked. Three workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (1 blocker), every blocker and should-fix closed with tests, integration head re-run in CI before main moved. A background heartbeat kept the LOCK fresh during long CI waits.

```
IMPLEMENTADO:
- P4-06 (Phase 4): DocDefaults window tags per level plan and, on levels with a tagged window, one linear dimension chain per exterior wall run (per-building ray test incl. L-shapes, separate buildings and site walls; perpendicular end walls as the two references; 1 m outside; wall face chosen from draw direction, mismatching ends skipped). Window-less projects byte-identical to P4-04.
- P6-09 (Phase 6): DXF MINSERT arrays expand row-major under the explode limits with key[row,column] provenance; door/window cells become one CadToBimIr opening each; AutoCAD arbitrary-axis OCS — near-±Z (≤1e-6) kept, tilted flat straight polylines moved to WCS, other tilted entities typed unsupported (ocs-non-planar / ocs-invalid).
- P7-07 (Phase 7): PrototypePipeline.Run/Rank — prototype → circulation → facade → BIM-IR windows gates, typed rejections (gate, code, subject, detail), accepted ranked 1..k, all refusals merged, canonical JSON with options.
- Board: P4-06, P6-09, P7-07 ✅; new P7-08 staggered-plan snap (no wall_clash), P7-09 generate-prototypes CLI verb, P7-10 facade quality ranking ⏳.
TESTEADO AUTOMÁTICAMENTE (backend):
- P4-06 tests/doc-regeneration: red 41965712152, green 41966585544; review red 41973671243, green 41974540346. 0 failed attempts.
- P6-09 tests/cad: red 41965721659, green 41966656787; review red 41968502384, green 41969132742. 0 failed attempts.
- P7-07 tests/generative: red 41965200434, green 41965816230; review red 41971196839, green 41972006351. 0 failed attempts.
- Merged integration head re-run in CI before main moved (see journal).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session this relay).
BLOQUEADO / POR QUÉ: nothing new blocked.
DECISIONES QUE NECESITO DE VOS: nothing new; still open — escritorio/estudio/biblioteca class; Uniclass excerpt licence; party-wall facade edges.
BENCHMARKS / NÚMEROS REALES: P7-07 on the 16 real 1–3-bedroom prototypes: 6 accepted, 6 duplicate_geometry, 4 wall_clash (8 issues).
GAPS CONOCIDOS:
- P4-06: never run in Revit; two references per chain (no intermediate points); face choice assumes unflipped walls; courtyard facades skipped; curved walls not handled.
- P6-09: tilted circles/arcs/text/inserts not projected; CadDraftSource has no row/column; digests rounded to 1e-6; removed warning codes ocs-unsupported/minsert-ignored.
- P7-07: facade is pass/fail (P7-10); wall_clash on staggered plans (P7-08); not in CLI/MCP (P7-09).
ARCHIVOS CAMBIADOS: core/Bim.Core/{DocDefaults,DocDefaultsDimensions}.cs; adapters/cad/Bim.Cad/{DxfModel,DxfReader,DxfExploder,DxfJson,CadToBimIr,BriefExtract}.cs; core/Bim.Generative/PrototypePipeline{,Model,Serializer}.cs; tests/doc-regeneration/{DefaultsWindows,DefaultsP404Reference,Program}.cs; tests/cad/{MinsertOcsTests,Program}.cs + fixtures/minsert-ocs.dxf; tests/generative/{PrototypePipelineTests,Program}.cs; board/features/contracts/attempts/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); dotnet run --project tests/doc-regeneration; dotnet run --project tests/cad; dotnet run --project tests/generative.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P2-08/P2-09, P5-08, P10-08/P10-09 (relay156); then P1-10/P1-11, P3-06/P3-07, P8-08/P8-09, P9-06 port/P9-03 (relay157; check origin chat-* branches before any Revit command file — chat-wall-attach still unmerged); then P4-07, P6-08, P7-08..P7-10.
```

Owner: no new Revit step from this relay (all headless).
