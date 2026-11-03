# Relay142: documentation commands, plan runner and glTF export

Cloud scheduled relay, started 2026-11-03 11:53Z. GitHub CI was the only oracle (no local .NET SDK). Three tasks from the phases touched longest ago: Phase 4, Phase 3 and Phase 8 (P1-05 deferred: shares RevitCommandsCatalogue.cs and CommandsExecutor.cs with P4-02). New Phase 8 batch loaded (P8-04 done; P8-05, P8-06 ⏳; P8-07 ⛔ on P8-04 → now ⏳-eligible next relay). Each task went red in CI, then green, then through an independent read-only review; no blockers, all should-fix items closed with tests (red then green). Relay143 briefly took over the lock as stale (heartbeat gap during review fixes) and backed off after seeing live commits; relay142 resumed.

```
IMPLEMENTADO:
- P4-02 (Phase 4): query.title_blocks, dimension.create_linear (plan views, two parallel grids/walls, wall face, perpendicular tolerance), tag.create (by category; leader requires head), schedule.create (fields, sort), sheet.create (title block, unique number), sheet.place_view (viewport or schedule sheet instance; legends allowed on many sheets) on the catalogue envelope with copies-only guard, operation_id receipts and readback with rollback; MCP/audit tables and module contract updated.
- P3-03 (Phase 3): PlanRunner — runs a RevitCommandPlan through an injected transport: level preconditions first, host placeholders from created[0], strict receipt matching, stop on first failure with a deterministic resumable JSON report, resume re-sends only pending steps relying on operation_id replay; tampered reports refused.
- P8-04 (Phase 8): adapters/gltf/Bim.Gltf — BCL-only deterministic BIM-IR → glTF 2.0 (.glb, .gltf) for Blender import: walls, spaces, levels, extras {bim_id, kind, type, level}, Z-up→Y-up, never-overwrite writer.
TESTEADO AUTOMÁTICAMENTE (backend):
- P4-02 tests/revit-commands/CatalogueDocumentation.cs: red 41632823946, green 41635488841; review fixes red 41645665069, green 41647155908. 1 failed attempt (Point2? struct CS1503 in native-build).
- P3-03 tests/plan-to-commands/PlanRunnerTests.cs: red 41632184440, green 41633899232; review fixes red 41639513716, green 41640877159. 1 failed attempt (CS0136 test local).
- P8-04 tests/gltf (new project in verify.ps1, in-test glTF spec validator with mutation tests, golden SHA-256 equal on Linux/Windows): red 41632463739, green 41633796727; review fixes red 41642732753, green 41644141785. 0 failed attempts.
- The merged integration head runs CI again before main moves.
VERIFICADO EN REVIT NATIVO: nothing new.
BLOQUEADO / POR QUÉ: nothing new blocked. Documentation commands need an owner Revit session on a test copy; glTF output has not been opened in Blender (not installed).
DECISIONES QUE NECESITO DE VOS: none. No new dependencies, no BIM-IR schema or persisted-format change.
BENCHMARKS / NÚMEROS REALES: none.
GAPS CONOCIDOS:
- Documentation: never run in Revit; no schedule level filter, tag type choice, section-view dimensions/tags; one wall_face for both references.
- Plan runner: fake transport only, no concrete pipe adapter class; created_missing does not clear on resume.
- glTF: no CLI verb (P8-07), no wall joins, BIM-IR carries no openings/slabs; not checked by the Khronos validator.
ARCHIVOS CAMBIADOS: core/Bim.Core/RevitCommandsDocumentation.cs, RevitCommandsCatalogue.cs, PlanRunner.cs; runtimes/revit-native/CommandsDocumentation.cs, CommandsExecutorDocumentation.cs, CommandsExecutor.cs, CommandsExecutorStructure.cs; adapters/gltf/**; tests/revit-commands/**, tests/mcp/Program.cs, tests/audit/Program.cs, tests/plan-to-commands/**, tests/gltf/**; scripts/verify.ps1; docs/contracts/module-contracts.md; tasks.md; docs/features.json; docs/agent/ATTEMPTS.md; docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK) — runs revit-commands, plan-to-commands and gltf tests; dotnet build runtimes/revit-native/Bim.Revit.csproj --configuration Release --warnaserror.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest-touched phases: P1-05 modify/delete (Phase 1), P6-04 CAD→BIM-IR draft (Phase 6), P7-03 prototype generator (Phase 7) or P10-04/P10-05 (Phase 10); then P8-05/P8-06/P8-07, P2-04/P2-05, P3-04, P5-04/P5-05, P9-03/P9-04.
```

## Owner probes
- Revit (test copy with two parallel grids, a wall, a door and a Level 1 floor plan): query.title_blocks; dimension.create_linear between the grids (value_mm = spacing) and between two parallel walls with wall_face interior; tag.create on the wall with and without head (leader needs head); schedule.create {category:"door", fields:["Mark","Type"], sort_by:"Mark"}; sheet.create {number:"A101", name:"Test"}; sheet.place_view of the plan and the schedule at [420,297]; place the plan again → args_invalid naming A101; a legend on two sheets is allowed; replay one operation_id → same receipt.
- Blender (optional): `GltfExporter.WriteGlb(project, "room.glb")`, File → Import → glTF 2.0; walls upright in metres, Custom Properties show bim_id/kind/level.
