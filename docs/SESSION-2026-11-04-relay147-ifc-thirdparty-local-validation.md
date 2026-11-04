# Relay147: third-party IFC import, localized validation, Phase 9 owner-priority batch

Cloud scheduled relay, started 2026-11-03 22:53Z. GitHub CI was the only oracle (no local .NET SDK, no local IfcOpenShell). Owner priority from OWNER.md (2026-11-03 18:49-03:00) came first: a Phase 9 batch for materials, layered wall/floor types, extruded roofs, document save/save_as into enabled roots and DirectShape site elements. Then the oldest-touched phases: Phase 8 (P8-05) and Phase 3 (P3-04). Each task went red in CI, then green, then through an independent read-only review; every should-fix was closed with tests.

P9-06 (material.create, wall_type.create, floor_type.create) was built and reviewed on relay147-P9-06 (771cca4, CI 41740133186 green), but while it ran the owner chat pushed chat-materials-types (23:28–23:35Z) with the same command names plus material.set, query.fill_patterns and element.change_type. Two implementations of the same commands cannot both ship, and owner-chat command work has precedence, so relay147 did **not** merge P9-06. P9-06 stays ⏳ with the done-when "integrate the chat branch after independent review and port the relay147-P9-06 hardening as tests" (core/shell + structural index readback, ordinal-first name pick, 1.2 mm minimum layer, Revit ArgumentException → args_invalid at args.name).

```
IMPLEMENTADO:
- P8-05 (Phase 8): third-party IFC import report — per-entity entities/summary (imported/approximated/unsupported with reasons, additive to bim.ifc-import/1) for IFC4 and IFC2X3: IfcPolyline/IfcIndexedPolyCurve rectangular profiles, rigid identity-origin IfcMappedItem (Body and Axis), exact horizontal half-space clips; approximated (stated) sloped/bounded clips and bounding-rectangle wall profiles with fill ratio >= 0.9 (else profile_fill_too_low); host_approximated on openings in approximated walls; FootPrint space fallback; non-identity MappingOrigin refused (ISO vs kernel disagreement observed in CI).
- P3-04 (Phase 3): LocalValidation — after Compiler.Diff, rules run only on plan.DirtyIds; Merge(ValidateAll(before), local) == ValidateAll(after); invariant stated (rules read only the element and its prerequisites closure).
- Board: P9-06 (owner chat integration + hardening), P9-07 roof.create_extrusion, P9-08 document.save/save_as into enabled roots, P9-09 DirectShape site elements, all ⏳.
TESTEADO AUTOMÁTICAMENTE (backend):
- P8-05 tests/ifc/test_import_thirdparty.py (fixtures generated with ifcopenshell.api, kernel world-bounds oracle): red 41735930602, green 41738076157; review red 41741627711, green 41742335649 (ubuntu/windows/macos). 1 failed attempt (MappingOrigin composition).
- P3-04 tests/local-validation (new, 156 checks: fixed cases, 300 single-edit + 400 multi-edit seeded steps over 12 edit kinds, per-id non-vacuity, permuted order): red 41736650256, green 41737727224; review 41743664466, 41743701205. 0 failed attempts. One wall move on a 50-room model revalidates 6/551 elements.
- P9-06 (not merged): red 41735472864, green 41737339853; review red 41739987700, green 41740133186. 1 failed attempt.
- The merged integration head runs CI again before main moves.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session this relay).
BLOQUEADO / POR QUÉ: P9-06 not merged — duplicate of the owner chat's chat-materials-types; the next relay reviews and integrates the chat branch instead.
DECISIONES QUE NECESITO DE VOS: (1) OK to integrate chat-materials-types from a scheduled relay (after review), or will the chat merge it? (2) still open: escritorio/estudio/biblioteca class; Uniclass CC BY-ND excerpt.
BENCHMARKS / NÚMEROS REALES: localized validation 6/551 elements revalidated for one wall move (rule evaluations, not wall time); 3865/38314 over 400 multi-edit steps.
GAPS CONOCIDOS:
- IFC: IfcOpenShell-authored fixtures only (no Revit/ArchiCAD exports); MappingOrigin non-identity, scaled maps, arcs, voids, rotated spaces unsupported.
- Local validation: Compiler.Diff and graph still built whole; dirty set over-approximates; project-local rules only.
ARCHIVOS CAMBIADOS: runtimes/ifc/ifc_import.py, runtimes/ifc/README.md, docs/ifc.md, tests/ifc/test_import_thirdparty.py; core/Bim.Core/LocalValidation.cs, tests/local-validation/**, scripts/verify.ps1; docs/contracts/module-contracts.md; tasks.md; docs/features.json; docs/agent/ATTEMPTS.md; docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); python -m unittest tests/ifc/test_import_thirdparty.py with IfcOpenShell (see docs/ifc.md).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: review + integrate chat-materials-types (P9-06) with the relay147-P9-06 hardening; then P9-07/P9-08/P9-09, P9-03/P9-04, P8-06/P8-07, P4-03, P6-05, P7-04/P7-05, P1-07/P1-08, P2-05, P5-04/P5-05, P10-04.
```
