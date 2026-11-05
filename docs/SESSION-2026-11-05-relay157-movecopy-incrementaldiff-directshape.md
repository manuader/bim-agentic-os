# Relay157: element move/copy, incremental diff from an edit set, DirectShape site solids

Cloud scheduled relay, started 2026-11-05 00:53Z. GitHub CI was the only oracle (no local .NET SDK). The oldest-touched phases were 1, 3, 8 and 9 (relay153); P1-09, P3-05 and P9-09 were picked (Phase 8 had no ready task, so its next batch was loaded instead). Three workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (no blockers), every should-fix closed with tests, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P1-09 (Phase 1): element.move / element.copy — 1–100 elements by a mm vector under the copies-only guard, typed refusals (element_pinned, element_view_specific, element_in_group, host_missing, location_unsupported), hosted targets pruned when their host moves, readback of location point+rotation / curve ends / sketch profile / bbox within 1 mm, copies matched to sources by bipartite nearest match; batch step; MCP tools.
- P3-05 (Phase 3): IncrementalDiffIndex — plan byte-identical to Compiler.Diff from an edit set, serializing only edited elements and traversing only the dirty closure; honest Traversed counter; typed edit_* refusals, invalid_ir precedence.
- P9-09 (Phase 9): direct_shape.create — extruded polygon or box DirectShape in generic_model/site/planting/entourage/topography, material, name, bbox/volume/category/material readback; batch step; MCP tool.
- Board: P1-09, P3-05, P9-09 ✅; new P3-06 advance index, P3-07 diff-edits CLI verb, P8-08 IFC schema validation, P8-09 .3dm openings ⏳.
TESTEADO AUTOMÁTICAMENTE (backend):
- P1-09 tests/revit-commands + tests/mcp + tests/audit: reds 41945946857/41946946583/41948185837, green 41949355724; review red 41951385049, green 41953640792. 1 failed attempt (copy pairing).
- P3-05 tests/local-validation: red 41945296954, green 41947771055; review red 41955010456, green 41956142359. 1 failed attempt (NaN before invalid_ir).
- P9-09 tests/revit-commands: red 41945858175, green 41946817036; review red 41957340069, green 41959286079. 0 failed attempts.
- Merged integration head re-run in CI before main moved (see journal).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session this relay).
BLOQUEADO / POR QUÉ: nothing new blocked.
DECISIONES QUE NECESITO DE VOS: nothing new; still open — escritorio/estudio/biblioteca class; Uniclass excerpt licence; party-wall facade edges.
BENCHMARKS / NÚMEROS REALES: P3-05 one wall move in 551- and 2201-element models: Visited 6, Serialized 1, Traversed identical (size-independent).
GAPS CONOCIDOS:
- P1-09: never run in Revit; joined-wall rigidity, hosted openings copied with walls, one-sketch assumption for floors/roofs, face-based orphans, neighbours Revit modifies not reported; no dry_run.
- P3-05: AfterHash and After assembly O(n); Build O(project); index not advanced (P3-06).
- P9-09: never run in Revit; accepted categories (topography doubtful), bbox padding, SolidOptions material, SetName readback; not associated with the level.
ARCHIVOS CAMBIADOS: core/Bim.Core/{RevitCommandsTransform,RevitCommandsDirectShape,IncrementalDiff}.cs; runtimes/revit-native/{CommandsTransform,CommandsExecutorTransform,CommandsDirectShape,CommandsExecutorDirectShape}.cs; registries (RevitCommandsCatalogue/Batch/Modify, CommandsExecutor); tests/revit-commands/{CatalogueTransform,CatalogueDirectShape,CatalogueExportSave,CatalogueModify,Program}.cs + csproj; tests/local-validation/{IncrementalDiffTests,Program}.cs; tests/mcp, tests/audit; capabilities/revit-parity-{census.json,summary.md}; docs/revit-playbook.md; board/features/contracts/attempts/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); dotnet run --project tests/revit-commands; dotnet run --project tests/local-validation; dotnet run --project tests/parity-census.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P4-06/P4-07, P6-08/P6-09, P7-07 (relay155); then P2-08/P2-09, P5-08, P10-08/P10-09 (relay156); then P1-10/P1-11, P3-06/P3-07, P8-08/P8-09, P9-06 port/P9-03 (relay157; check origin chat-* branches before any Revit command file — chat-wall-attach still unmerged).
```

Owner smoke test (test copy BIM-Agentic-Test*.rvt only, Load Once): see OWNER-TODO Relay157.
