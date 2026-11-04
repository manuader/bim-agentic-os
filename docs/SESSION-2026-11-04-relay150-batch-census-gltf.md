# Relay150: command.batch, Revit parity census, glTF export verb with openings

Cloud scheduled relay, started 2026-11-04 05:53Z. GitHub CI was the only oracle (no local .NET SDK). The oldest-touched phases were 1 (relay145) and 3, 8, 9 (relay147); P1-07, P9-04 and P8-07 were picked (P1-08/P9-07/P9-09 left: the owner chat's document.save and Revit command work overlap them). Each task went red in CI, then green, then through an independent read-only review (no blockers; should-fix closed with tests in one round).

```
IMPLEMENTADO:
- P1-07 (Phase 1): command.batch — 1–50 catalogue write steps in one TransactionGroup (one undo entry), per-step schema validation (args.steps[i] paths), Autodesk-free RevitCommandBatch.Run (stop at first failure, total rollback, survivor check, receipt-room precheck, generic dry-run refusal), batch receipt + replay/conflict, MCP revit_command_batch (destructive).
- P9-04 (Phase 9): capabilities/revit-parity-census.json (103 functions, 11 areas, all 54 catalogue commands, feature_coverage map) + generated revit-parity-summary.md, enforced by tests/parity-census.
- P8-07 (Phase 8): export-gltf CLI verb (bounded input, exclusive create, link/device/NTFS-stream refusals) and BIM-IR door/window cuts in the host wall plane (transoms, stacked windows), cross-OS goldens.
- Integration: census entry modify.batch.one_undo added for command.batch (merged after the census); features.json entries revit-command-batch (compiled), revit-parity-census and gltf-export-cli-openings (unit-tested).
TESTEADO AUTOMÁTICAMENTE (backend):
- P1-07 tests/revit-commands CatalogueBatch (+ mcp/audit catalogue loops): red 41783202390, green 41786172875; review red 41788320098, green 41789389908. 0 failed attempts.
- P9-04 tests/parity-census (gate parity-census-tests): red 41782777772, green 41783619497; review red 41790838549, green 41792117807. 0 failed attempts.
- P8-07 tests/gltf + tests/cli: red 41782803843, green 41785106385; review red 41794045481, green 41797924226. 1 failed attempt (validator centroid per connected piece).
- The merged integration head runs CI again before main moves.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session this relay).
BLOQUEADO / POR QUÉ: nothing new blocked.
DECISIONES QUE NECESITO DE VOS: still open — escritorio/estudio/biblioteca class; Uniclass CC BY-ND excerpt; whether to record the owner chat's 2026-11-03/04 Revit runs (docs/revit-playbook.md) as verified-in-revit evidence in features.json (needs an owner-attested scope line per command).
BENCHMARKS / NÚMEROS REALES: batch receipt for 50 family.load-sized steps < 0.4 MB (about 6 maximal batches per 4 MB receipt store); census counts 10 verified-in-revit, 4 unit-tested, 37 compiled, 52 unsupported.
GAPS CONOCIDOS:
- P1-07: executor never run in Revit — edit-scope steps (stairs, wall profile) inside nested groups and the single undo entry unverified; not yet in owner-catalogue-steps.
- P9-04: bookkeeping only; owner-chat uses noted, not claimed.
- P8-07: never opened in Blender/Khronos validator; rectangular cuts only; wall joins unresolved.
ARCHIVOS CAMBIADOS: core/Bim.Core/{RevitCommandsBatch.cs,RevitCommandsCatalogue.cs,RevitCommands.cs,RevitCommandsModify.cs,RevitCommandsService.cs}, runtimes/revit-native/{CommandsExecutorBatch.cs,CommandsExecutor.cs}, tests/revit-commands/{CatalogueBatch.cs,CatalogueExportSave.cs,CatalogueModify.cs,Program.cs}, tests/mcp/Program.cs; capabilities/revit-parity-{census.json,summary.md}, tests/parity-census/**, scripts/verify.ps1; adapters/gltf/{Bim.Gltf/GltfExporter.cs,README.md}, interfaces/cli/{GltfExportCommand.cs,Program.cs,Bim.Cli.csproj}, tests/gltf/Program.cs, tests/cli/{Program.cs,Bim.Cli.Tests.csproj}, scripts/package.ps1, tests/packaging/test-package.ps1; docs/contracts/module-contracts.md; tasks.md; docs/features.json; docs/agent/ATTEMPTS.md; docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); dotnet run --project tests/parity-census -- --write-summary; dotnet run --project interfaces/cli -- export-gltf project.json out.glb.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: P1-08 document.save_copy, P9-06 hardening port, P9-07 roof.create_extrusion, P9-09 DirectShape, P9-03 free path (check origin chat-* branches first; chat-wall-attach still unmerged and unreviewed), P8-06, P3-05, P4-04/P4-05, P6-06/P6-07, P7-05, P5-04, P2-06/P2-07, P10-06/P10-07.
```
