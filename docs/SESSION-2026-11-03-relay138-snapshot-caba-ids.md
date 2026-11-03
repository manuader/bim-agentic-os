# Relay138: Revit→BIM-IR snapshot, CABA rule pack and IDS checks

Cloud scheduled relay, started 2026-11-03 02:53Z. GitHub CI was the only oracle (no local .NET SDK). Three tasks from the phases touched longest ago: Phase 2, Phase 5 and Phase 10. Each went red in CI, then green, then through an independent read-only review; every review blocker and should-fix sent back was closed with tests (red then green). The container restarted once mid-session; all work was already pushed and the P5-02 fix run finished green afterwards.

```
IMPLEMENTADO:
- P2-01 (Phase 2): read-only Revit command query.snapshot. Pure mapper core/Bim.Core/Mirror.cs + MirrorMapper.cs (neutral observed records → existing BIM-IR level/wall/door/window/space elements, ids = SHA-256 of document+element UniqueId, deterministic canonical JSON, non-persisted `sources` and `unmapped` sections with reasons). Revit collector runtimes/revit-native/Mirror.cs (no transactions; Level.Elevation; primary design option only; demolished elements listed as unmapped; cap 20000 observed records → snapshot_too_large). Walls whose location line is not the centreline, curtain, stacked and base-offset walls, non-rectangular rooms, floors (no BIM-IR kind) and doors with sill ≠ 0 are reported unmapped, never mapped lossily. No schema change.
- P5-02 (Phase 5): skills/ar.caba.codigo-edificacion (id needs reverse-DNS form, so not skills/caba-codigo-edificacion) with 95 room rules citing Código de Edificación CABA / RT-030809-020309-01 v4 (Res. 166/SSGU/2023), each with source_confidence cited|to-verify inside its citation string (schema unchanged). core/Bim.Core/SkillRoomValidator.cs: RoomFacts (typed units, FromProject with approximate clear height flagged), min/max/ratio_min, NotEvaluable never passes, unmatched rooms block Compliant, bounded parameter overrides (corredor_lado_min_m), area-scoped heights.
- P10-01 (Phase 10): runtimes/ifc/ids_check.py, an IDS 1.0 subset checker (entity/attribute/property/classification facets, simpleValue/enumeration/pattern with XSD-safe translation, restriction base typing, SI unit conversion, cardinalities), deterministic JSON report, exit 0 pass / 1 fail / 2 invalid; DOCTYPE/ENTITY refused; unsupported features fail closed.
TESTEADO AUTOMÁTICAMENTE (backend):
- P2-01 tests/revit-commands/MirrorSnapshot.cs: red 41569410455, green 41570410565; review fixes red 41572760325, green 41573858213 (1 failed attempt: cap fixture UniqueIds collided with level UniqueIds; test data fix, assertion unchanged). native-build compiles the collector against Revit 2025 reference assemblies.
- P5-02 tests/skills: red 41570421785, green 41571379423 (1 failed attempt: test helper type CS0029); review fixes red 41576696994, green 41576795164.
- P10-01 tests/ifc/test_ids.py with real IFC exported by worker.create: red 41569326884, green 41569990272; review fixes red 41575148096, green 41575714191 (ifc job + ifc-portable on 3 OS).
- The merged integration head runs CI again before main moves.
VERIFICADO EN REVIT NATIVO: nothing new.
BLOQUEADO / POR QUÉ: query.snapshot needs an owner-attended Revit session on a test copy to move past compiled for the collector.
DECISIONES QUE NECESITO DE VOS: review the CABA values marked to-verify (heights 2.60/2.40/2.10, illumination x=10, ventilation k=i/3, class of dormitorio) against Ley 6.100 and its RT. No new dependencies.
BENCHMARKS / NÚMEROS REALES: none.
GAPS CONOCIDOS:
- Snapshot: no floor kind in BIM-IR (ADR needed to add one), no linked models, wall top constraints not verified, rotated buildings lose rooms (room_not_rectangular), cap instead of pagination.
- CABA: second-class heights missing; 1.70 m circle rule, patios and duct rules not expressible; BIM-IR rooms have no names/openings (names supplied by caller; light/vent not evaluable on model data); not wired into the P7-01 planner.
- IDS: no partOf/material facets, quantities, numeric bounds/length restrictions or subtype matching; not run against the official buildingSMART test corpus or XSD.
ARCHIVOS CAMBIADOS: core/Bim.Core/Mirror.cs, MirrorMapper.cs, RevitCommandsCatalogue.cs, SkillRoomValidator.cs; runtimes/revit-native/Mirror.cs, CommandsExecutorViews.cs; runtimes/ifc/ids_check.py; skills/ar.caba.codigo-edificacion/skill.json; tests/revit-commands/**, tests/skills/Program.cs, tests/ifc/test_ids.py, tests/ifc/fixtures/ids/*; docs/ifc.md; tasks.md; docs/features.json.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); python -m unittest discover -s tests/ifc -p 'test_*.py' (approved ifcopenshell venv); python runtimes/ifc/ids_check.py SPEC.ids MODEL.ifc.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: P1-02 element query (Phase 1), P3-02 plan→catalogue bridge, P8-01 IFC import roundtrip; then P2-03, P4-02, P7-02, P10-02.
```
