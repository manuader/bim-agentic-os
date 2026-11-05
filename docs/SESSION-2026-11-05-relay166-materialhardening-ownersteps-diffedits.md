# Relay166: material/type hardening, owner session steps, diff-edits CLI

Cloud scheduled relay, started 2026-11-05 17:53Z. GitHub CI was the only oracle (no local .NET SDK or pwsh). Oldest-touched phases were 9 (relay157), 1 and 3 (relay161); P9-06 (hardening port), P1-11 and P3-07 were picked (P8-09 deferred). Three workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (0 blockers), every should-fix and most NITs closed with tests, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P9-06 (Phase 9): relay147 hardening ported onto the owner chat's material/type commands — exact-name-first PickExisting for materials and wall/floor/roof types; non-membrane layers < 1.2 mm refused length_invalid before the pipe (wall/floor/roof); readback of exterior/interior shell counts and structural material index (first structure layer) for wall, floor and roof types; Revit ArgumentException on Duplicate/Material.Create → args_invalid at args.name.
- P1-11 (Phase 1): owner -Catalogue run gains groups formas (3), extrusion (5), transformar (9), copia (5) — 103 steps; modificar runs on the saved copy; a failed save after SaveAs is detected by a copy probe; closing message tells the owner to answer No at Revit's save prompt; docs/owner-verification-session.md updated.
- P3-07 (Phase 3): `diff-edits --project <p> --edits <e>` CLI verb over IncrementalDiff — byte-identical to the library and to `diff` on the applied project, bounded inputs, unknown members/duplicate keys refused, typed refusals exit 1, inputs never written.
- Board: P1-11, P3-07, P9-06 ✅; new P1-12 (catalogue-report CLI), P1-13 (query.elements cursor), P3-08 (apply-edits with store commit), P3-09 (intent edits → element edit set) ⏳.
TESTEADO AUTOMÁTICAMENTE (backend):
- P9-06 tests/revit-commands: red 42079534722, green 42081753813; review red 42088743974, green 42088830631. 0 failed attempts.
- P1-11 tests/integration/test-catalogue-session.ps1 (fake pipe + real CLI, ubuntu + windows): red 42081608585, green 42084994501; review red 42091583347, green 42092176694. 1 failed attempt (fake reused the request sha variable).
- P3-07 tests/cli: red 42081207010, green 42082226772; review red 42094495789, green 42095406642. 0 failed attempts, 1 compile slip.
- Merged integration head re-run in CI before main moved (see journal).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: nothing new blocked; P1-11 steps are described until the owner runs -Catalogue.
DECISIONES QUE NECESITO DE VOS: nothing new; still open — escritorio/estudio/biblioteca class; Uniclass excerpt licence; party-wall facade edges.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P9-06: executors never run in Revit (shell counts/structural index after SetCompoundStructure, 1.2 mm for every layer function, which names Duplicate refuses); hand-made same-name types without a structural material layer are now refused.
- P1-11: whether SaveAs re-points the open document, OST_Site DirectShape acceptance, roof box within 25 mm with type thickness, door-rotation refusal on a real door — all owner-run only; fake roof geometry uses the script's own formula.
- P3-07: index rebuilt per run (O(project)); no --max-affected; refusal does not name the failing file; internal-error path only statically checked.
ARCHIVOS CAMBIADOS: core/Bim.Core/{RevitCommandsMaterialTypes,RevitCommandsMaterials}.cs; runtimes/revit-native/{CommandsMaterialTypes,CommandsExecutorMaterialTypes,CommandsMaterials,CommandsExecutorMaterials}.cs; tests/revit-commands/{CatalogueMaterialTypes,CatalogueMaterials}.cs; scripts/{owner-catalogue-steps,owner-native-session}.ps1; tests/integration/{test-catalogue-session,fake-revit-command-executor,fake-revit-command-pipe}.ps1; docs/owner-verification-session.md; interfaces/cli/{DiffEditsCommand,Program}.cs; tests/cli/Program.cs; docs/contracts/module-contracts.md; board/features/attempts/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); dotnet run --project tests/revit-commands; dotnet run --project tests/cli; pwsh -File tests/integration/test-catalogue-session.ps1.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P4-08/P4-09, P6-10/P6-11, P7-09/P7-10 (relay162); then P2-10/P2-11, P5-10/P5-11, P10-10/P10-11 (relay164); then P8-09, P9-03/P9-08, P1-12/P1-13, P3-08/P3-09 (check origin chat-* branches before any Revit command file — chat-wall-attach still unmerged).
```

Owner: the one -Catalogue run now also exercises save_copy, extrusion roofs, direct shapes and move/copy/rotate/mirror (103 steps). Command unchanged: `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-native-session.ps1 -Catalogue` (Load Once); at the end answer **No** to Revit's save prompt so the saved copy keeps its recorded sha256.
