# Relay151: default documentation set, DXF attributes/MTEXT room tags, facade openings

Cloud scheduled relay, started 2026-11-04 08:53Z. GitHub CI was the only oracle (no local .NET SDK). The oldest-touched phases were 3, 4, 6 and 7 (relay148); P4-04, P6-06 and P7-05 were picked (all headless, no Revit command files, no overlap with the owner chat's chat-* branches). Each task went red in CI, then green, then through an independent read-only review: one blocker each, closed with tests; re-review no blockers; remaining should-fix closed in a further round.

```
IMPLEMENTADO:
- P4-04 (Phase 4): DocDefaults.From(project) — deterministic default DocumentationSet (plan + sheet per level, space/door tags, door and room schedules on sheet A900), bound to BIM-IR plan/sheet/schedule elements; hosted doors tagged only with exactly one host wall, in the host wall's level plan.
- P6-06 (Phase 6): DXF ATTRIB/ATTDEF with provenance, INSERT 66/SEQEND, constant ATTDEFs only, TEXT/MTEXT/%%/caret decoding, room-tag blocks feed brief extraction (guarded against title blocks/furniture), ft² on imperial drawings, explode-limit fallback keeps top-level attributes.
- P7-05 (Phase 7): FacadePlanner — windows on exterior room parts sized to the CABA illumination/ventilation ratio rules (cited rule ids), party-wall OpeningEdges, internal-void flag, applies_if, parameter overrides, typed failures, bounded options.
- Board: new Phase 7 batch P7-06 (facade windows into prototype BIM-IR, ⏳) and P7-07 (prototype pipeline with circulation + facade gates, ⛔ on P7-06).
TESTEADO AUTOMÁTICAMENTE (backend):
- P4-04 tests/doc-regeneration Defaults: red 41804430030, green 41805667983; review red 41808247828, green 41809487719. 0 failed attempts.
- P6-06 tests/cad AttributeTests: red 41804007889, green 41806106763; review red 41810785252, green 41811167558; final red 41815426981, green 41815912317. 0 failed attempts.
- P7-05 tests/generative FacadeTests: red 41804098606, green 41805103737; review red 41812510945, green 41814107348 (1 failed attempt, double slack); final red 41816874404, green 41817395508.
- The merged integration head runs CI again before main moves.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session this relay).
BLOQUEADO / POR QUÉ: nothing new blocked.
DECISIONES QUE NECESITO DE VOS: still open — escritorio/estudio/biblioteca class; Uniclass CC BY-ND excerpt; recording the owner chat's Revit runs as verified-in-revit in features.json. New, optional: for lots between party walls, confirm that only front and back (contrafrente) footprint edges may take illumination windows (P7-05 OpeningEdges default is all four edges).
BENCHMARKS / NÚMEROS REALES: MTEXT decoding linear (100,000-deep brace nesting and 20,000 ATTRIBs tested); facade sizing bounded (10 km rooms refused as edge_too_short before mm arithmetic).
GAPS CONOCIDOS:
- P4-04: never run in Revit; no dimensions/sections/3D/window tags; sheet numbers follow elevation order.
- P6-06: hand-written fixture only; no MINSERT/tilted OCS/MTEXT fields; built-in tag word lists.
- P7-05: layout only (BIM-IR windows are P7-06); entrance door overlap; no wall thickness or patio minimums.
ARCHIVOS CAMBIADOS: core/Bim.Core/DocDefaults.cs, tests/doc-regeneration/{Defaults.cs,Program.cs}; adapters/cad/Bim.Cad/{DxfModel,DxfReader,DxfExploder,DxfJson,BriefExtract,BriefExtractModel}.cs, tests/cad/{AttributeTests.cs,Program.cs,fixtures/room-tags.dxf}; core/Bim.Generative/{Facade,FacadePlanner,FacadeSerializer}.cs, tests/generative/{FacadeTests.cs,Program.cs}; docs/contracts/module-contracts.md; tasks.md; docs/features.json; docs/agent/ATTEMPTS.md; docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); dotnet run --project tests/doc-regeneration; dotnet run --project tests/cad; dotnet run --project tests/generative.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: P2-06/P2-07, P10-06/P10-07, P5-04 (oldest touched now), then P7-06, P4-05, P6-07, P3-05, P8-06; Revit command items P1-08, P9-06 hardening port, P9-07, P9-09, P9-03 (check origin chat-* branches first).
```
