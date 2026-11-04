# Relay153: save a test copy, Rhino .3dm draft import, extrusion roofs

Cloud scheduled relay, started 2026-11-04 13:53Z. GitHub CI was the only oracle (no local .NET SDK). The oldest-touched phases were 1, 8 and 9 (relay150); P1-08, P8-06 and P9-07 were picked (no overlap with the owner chat's chat-* branches; only chat-wall-attach is unmerged and touches wall attach). Each task went red in CI, then green, then through an independent read-only review: P8-06 and P9-07 had one blocker each, closed with tests; a re-review found one more P9-07 blocker (a correct roof refused on a box tie), closed with a test. All should-fix items were closed with tests.

```
IMPLEMENTADO:
- P1-08 (Phase 1): document.save_copy — saves the open BIM-Agentic-Test*.rvt as a new BIM-Agentic-Test*.rvt under .bim/native-runs only; refusals decided in core before the pipe and repeated in the add-in (path shape, dotted segments, Revit backup names, outside_copies_root, source_not_test_copy, same_as_source, target_linked, folder_missing, target_exists); post-save location recheck; reply path/bytes/SHA-256; the open document becomes the copy (Revit SaveAs semantics); excluded from command.batch.
- P8-06 (Phase 8): adapters/rhino/rhino_draft.py — .3dm (rhino3dm 8.17, MIT) → non-persisted BIM-IR draft proposal (levels, walls, spaces) with per-object confidence, notes and a typed unmapped report; caps; NaN/inf refusals; deterministic bytes; roundtrip with the P8-02 writer.
- P9-07 (Phase 9): roof.create_extrusion — barrel/curved roofs from an open line/arc profile on the vertical plane through a plan line; core validation (connected, open, no self-intersection, no cusp, limits) before the pipe; executor with reference plane + NewExtrusionRoof; readback incl. expected-box wrong-side check; MCP tool; batch step.
- Board: P1-08, P8-06, P9-07 ✅ (Phase 1 board complete 8/8 → new batch P1-09 move/copy, P1-10 rotate/mirror, P1-11 owner-session steps for the new commands, all ⏳).
TESTEADO AUTOMÁTICAMENTE (backend):
- P1-08 tests/revit-commands CatalogueSaveCopy + tests/mcp: red 41843728108, green 41845412339; review red 41853561811, green 41854608933. 0 failed attempts.
- P8-06 tests/rhino (148 tests on real rhino3dm): red 41843469222, green 41847657825; reviews red 41858210496/41873773325, green 41862605911/41875504076. 1 failed attempt.
- P9-07 tests/revit-commands CatalogueRoofExtrusion + mcp/audit/parity-census: red 41845762559, green 41847684744; reviews red 41850833106/41869343965, green 41851839555/41870326635. 0 failed attempts.
- The merged integration head runs CI again before main moves.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session this relay).
BLOQUEADO / POR QUÉ: nothing new blocked. Revit executors of document.save_copy and roof.create_extrusion are compiled only (owner steps in OWNER-TODO).
DECISIONES QUE NECESITO DE VOS: still open — escritorio/estudio/biblioteca class; Uniclass CC BY-ND excerpt; party-wall facade edges (P7-05). Nothing new.
BENCHMARKS / NÚMEROS REALES: rhino draft collinear-vertex reduction 200k vertices < 1 s in CI; no other timing benchmarks.
GAPS CONOCIDOS:
- P1-08: SaveAs re-pointing, IsModified, source untouched, no upgrade dialog, hash while Revit holds the copy — unverified in Revit. General Save As of real projects still unsupported.
- P8-06: poly curves, block references, meshes, Breps unsupported; spaces axis-aligned rectangles only; not run in Rhino itself; rhino3dm segfaulted once intermittently.
- P9-07: extents along the plane normal, EXTRUSION_START/END_PARAM readback, view choice, unsplit profile curves unverified; a box holding both sweeps can't reveal the side; parity census entries stay at compiled (feature ledger entries added as compiled).
ARCHIVOS CAMBIADOS: core/Bim.Core/{RevitCommandsSaveCopy,RevitCommandsRoofExtrusion,RevitCommands,RevitCommandsCatalogue,RevitCommandsBatch,RevitCommandsService}.cs; runtimes/revit-native/{CommandsExecutorSaveCopy,CommandsRoofExtrusion,CommandsExecutorRoofExtrusion,CommandsExecutor}.cs; tests/revit-commands/{CatalogueSaveCopy,CatalogueRoofExtrusion,CatalogueBatch,CatalogueExportSave,Program}.cs; tests/mcp/Program.cs; tests/audit/Program.cs; adapters/rhino/{rhino_draft.py,README.md}; tests/rhino/{test_rhino_draft.py,run.py}; capabilities/revit-parity-{census.json,summary.md}; docs/contracts/module-contracts.md; docs/revit-playbook.md; tasks.md; docs/features.json; docs/agent/ATTEMPTS.md; docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); dotnet run --project tests/revit-commands; dotnet run --project tests/mcp; python -I tests/rhino/run.py (venv with requirements/rhino.txt).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — Phase 4 P4-05, Phase 6 P6-07, Phase 7 P7-06 (relay151); then P2-07, P5-07, P5-08, P10-07 (relay152), P3-05, P1-09/P1-10/P1-11, P8 next batch, P9-06 port/P9-09/P9-03.
```
