# Relay161: rotate/mirror, incremental index advance, IFC schema validation

Cloud scheduled relay, started 2026-11-05 08:52Z. GitHub CI was the only oracle (no local .NET SDK, PyPI blocked so no local ifcopenshell). The oldest-touched phases were 1, 3 and 8 (relay157); P1-10, P3-06 and P8-08 were picked. Three workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (0 blockers, 12 should-fix), every should-fix closed with tests, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P1-10 (Phase 1): element.rotate (vertical axis through a plan point, degrees CCW+) and element.mirror (vertical plane through a plan line, keep_original) for 1–100 elements under the copies guard; typed refusals incl. element_not_rotatable, element_not_mirrorable, angle_too_small_for_target, mirror_image_unobservable; ordered/unordered location readback within 1 mm, Mirrored flag toggle, replaced originals reported as deleted; batch step; MCP tools revit_element_rotate / revit_element_mirror; census modify.rotate_mirror compiled, modify.array unsupported.
- P3-06 (Phase 3): IncrementalDiffIndex.Advance(result) and Advance(result, committed) — next index without re-serializing untouched elements, equal to Build in every observable; typed edit_stale_result / edit_result_mutated / edit_commit_mismatch.
- P8-08 (Phase 8): runtimes/ifc/ifc_validate.py — bim.ifc-validate/1 JSON over ifcopenshell.validate (express rules) on a private capped copy; every IFC our exporters write in CI has zero issues; deliberately broken files typed.
- Board: P1-10, P3-06, P8-08 ✅. No new batch needed (P1-11, P3-07, P8-09 still ⏳).
TESTEADO AUTOMÁTICAMENTE (backend):
- P1-10 tests/revit-commands + tests/mcp + tests/audit: red 42000494415, green 42001156947; review red 42005842348, green 42007394205. 0 failed test attempts (3 compile slips).
- P3-06 tests/local-validation: red 41998448544, green 41999504722; review red 42009289955, green 42010080321. 0 failed attempts.
- P8-08 tests/ifc (ubuntu/windows/macos): red 41999435339/42000427658, green 42002056141; review red 42012119306, green 42012222589. 1 failed attempt (duplicate-GlobalId anchor is platform-ordered).
- Merged integration head re-run in CI before main moved (see journal).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session this relay; all headless/compiled).
BLOQUEADO / POR QUÉ: nothing new blocked.
DECISIONES QUE NECESITO DE VOS: nothing new; still open — escritorio/estudio/biblioteca class; Uniclass excerpt licence; party-wall facade edges.
BENCHMARKS / NÚMEROS REALES: P3-06 one wall move on 50/200-room models: Reused n-1, Carried 1.
GAPS CONOCIDOS:
- P1-10: never run in Revit — RotateElements on mirrored instances, MirrorElements in-place vs replace, Mirrored flag on every point instance, hosted elements deleted with a replaced host; facing/hand orientation not compared; Array unsupported.
- P3-06: still O(project) reference copies; untouched-element mutation not detected; not wired to CLI (P3-07).
- P8-08: only what the pinned IfcOpenShell validator implements (no buildingSMART service rules, no geometry); worker.validate / ifc_import._schema_check still validate the opened model; report quotes model content.
ARCHIVOS CAMBIADOS: core/Bim.Core/{RevitCommandsRotateMirror,RevitCommandsCatalogue,RevitCommandsBatch,RevitCommandsModify,IncrementalDiff,IncrementalDiffAdvance}.cs, Bim.Core.csproj; runtimes/revit-native/{CommandsRotateMirror,CommandsExecutorRotateMirror,CommandsExecutor,CommandsTransform,CommandsExecutorTransform,CommandsContract}.cs; capabilities/revit-parity-{census.json,summary.md}; runtimes/ifc/ifc_validate.py; tests/{revit-commands,mcp,audit,local-validation,ifc}; docs/ifc.md; board/features/contracts/attempts/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); dotnet run --project tests/revit-commands; dotnet run --project tests/local-validation; .tools/ifc/bin/python -m unittest discover -s tests/ifc -p 'test_validate.py' -v; .tools/ifc/bin/python runtimes/ifc/ifc_validate.py model.ifc --output report.json.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P4-07, P6-08, P7-08..P7-10 (relay159); then P2-09, P5-09..P5-11, P10-08 (relay160); then P1-11, P3-07, P8-09, P9-06 port/P9-03 (relay161/157; check origin chat-* branches before any Revit command file).
```

Owner: no new Revit step from this relay (all headless). When convenient, P1-10's element.rotate/element.mirror can be added to the owner catalogue session (P1-11 lineage) to reach verified-in-revit. Stray remote branch relay161-P8-08-probe could not be deleted from the cloud (proxy 403); safe to delete.
