# Relay144: modify/delete commands, CAD→BIM-IR draft and apartment prototypes

Cloud scheduled relay, started 2026-11-03 14:53Z. GitHub CI was the only oracle (no local .NET SDK). Three tasks from the phases touched longest ago: Phase 1 (P1-05), Phase 6 (P6-04) and Phase 7 (P7-03). Each went red in CI, then green, then through an independent read-only review; no blockers, every should-fix item and most NITs closed with tests (red then green). P8-07 flipped to ⏳ (P8-04 done). New batch: P4-03 (Phase 4 board was complete), P6-05, P7-04, P7-05.

```
IMPLEMENTADO:
- P1-05 (Phase 1): element.set_parameter (typed length/area/angle/text/int/yes-no/element values, read-only and group refusals, readback, side-effect deletion refusal, also_modified report) and element.delete (dependents probe rolled back, dependents protected like targets, confirm_dependents exact, ≤1000 dependents, post-commit no-extra-deletions check, compact receipts); dry_run rolls back with no receipt; copies-only/link guards; MCP destructiveHint true only for these two.
- P6-04 (Phase 6): CadToBimIr.FromDxf/FromPdf → non-persisted CadDraft of BIM-IR walls and door/window openings with confidence, provenance and an uncertainty report; nested door blocks, opening overlap, core opening rules (door sill 0, within wall height, clear span), through-wall tees, early work bounds, finiteness checks.
- P7-03 (Phase 7): PrototypeGenerator — 1/2/3-bedroom templates → P6-03 briefs and ranked layouts on a lot; approved rooms/walls kept bit-identical across reconfiguration, moved layouts rejected with reasons, unsatisfiable approvals refused up front; hall uses the CABA corridor side; pinned canonical JSON digest.
TESTEADO AUTOMÁTICAMENTE (backend):
- P1-05 tests/revit-commands/CatalogueModify.cs, tests/mcp, tests/audit: red 41660307076, green 41662074736; review fixes red 41667700782, green 41668015221. 1 failed attempt (IntegerParameterValue name clash with the Revit API).
- P6-04 tests/cad/DraftTests.cs: red 41660007678, green 41664205509; review fixes red 41670421661, green 41672486856. 3 failed attempts on distinct tests (CS7036 bulk replace, test arithmetic slip, CS1061 missing property).
- P7-03 tests/generative/PrototypeTests.cs: red 41659442966, green 41660989459; review fixes red 41674157580, green 41675668145. 0 failed attempts.
- The merged integration head runs CI again before main moves.
VERIFICADO EN REVIT NATIVO: nothing new.
BLOQUEADO / POR QUÉ: nothing new blocked. Modify/delete need an owner Revit session on a test copy.
DECISIONES QUE NECESITO DE VOS: none. No new dependencies, no BIM-IR schema or persisted-format change (prototype JSON is a new additive format).
BENCHMARKS / NÚMEROS REALES: none (budget tests only: 1,600-room DXF draft; 100×100 m 3-bedroom prototype with 32 alternatives < 30 s).
GAPS CONOCIDOS:
- Modify/delete: never run in Revit; no integer enumerations or other measured numbers; DocumentChanged inside the group unverified; three full id collections per delete.
- CAD draft: generated fixtures only; heights/sills assumed; PDF openings need listed form objects; straight walls only; positional ids.
- Prototypes: rooms around approved ones share one free rectangle; no corridors/doors as elements (P7-04); level 0 only.
ARCHIVOS CAMBIADOS: core/Bim.Core/RevitCommandsModify.cs, RevitCommandsCatalogue.cs; runtimes/revit-native/CommandsExecutorModify*.cs, CommandsExecutor.cs; interfaces/mcp/McpServer.cs; adapters/cad/Bim.Cad/CadToBimIr*.cs, Bim.Cad.csproj; core/Bim.Generative/Prototype*.cs; tests/revit-commands/**, tests/mcp/Program.cs, tests/audit/Program.cs, tests/cad/**, tests/generative/**; docs/contracts/module-contracts.md; tasks.md; docs/features.json; docs/agent/ATTEMPTS.md; docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); dotnet build runtimes/revit-native/Bim.Revit.csproj --configuration Release --warnaserror.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest-touched phases: P10-04/P10-05 (Phase 10), P2-04/P2-05 (Phase 2), P5-04/P5-05 (Phase 5); then P9-03/P9-04, P8-05/P8-06/P8-07, P3-04, P4-03, P6-05, P7-04/P7-05.
```

## Owner probe (Revit 2025, a fixture copy, never production; sequential)
1. query.inspect a wall (UniqueId, Unconnected Height, Comments).
2. element.set_parameter {built_in: WALL_USER_HEIGHT_PARAM} {kind: length, mm: 3200} dry_run → changed true, modified empty, height unchanged. Again without dry_run → 3200 mm; resend same envelope → replayed.
3. {name: "Comments"} text; a read-only parameter (CURVE_ELEM_LENGTH) → args_invalid at args.parameter; a yes/no (Room Bounding) and an element parameter (WALL_BASE_CONSTRAINT → level UniqueId).
4. A wall type parameter in dry_run → instances in also_modified (shows whether DocumentChanged fires inside the group).
5. element.delete on a wall hosting a door, dry_run → door in dependents; with the door pinned → refused naming the door; real delete without confirm_dependents → args_invalid; with confirm_dependents → deleted lists wall + dependents.
6. Same command on a real-project path → document_not_enabled without reaching the pipe.
