# Relay139: element query, plan→catalogue bridge and IFC import roundtrip

Cloud scheduled relay, started 2026-11-03 04:53Z. GitHub CI was the only oracle (no local .NET SDK; pypi blocked, so no local IfcOpenShell). Three tasks from the phases touched longest ago: Phase 1, Phase 3 and Phase 8. Each went red in CI, then green, then through an independent read-only review (no blockers); every should-fix sent back was closed with tests (red then green).

```
IMPLEMENTADO:
- P1-02 (Phase 1): read-only Revit commands query.elements (category friendly/OST_ name and/or level, 1–16 parameters instance-then-type, limit ≤5000, ascending ids, truncated + truncated_reason limit|bytes, 4 MiB UTF-8 reply budget, level-only queries restricted to model categories) and query.inspect (all instance/type parameters with storage type, spec, flags, typed units mm/m2/m3/deg via version-insensitive core routing RevitParameterUnits.ForSpec). core/Bim.Core/RevitCommandsQuery.cs, runtimes/revit-native/CommandsExecutorQuery.cs; MCP tools revit_query_elements / revit_query_inspect (read level).
- P3-02 (Phase 3): core/Bim.Core/PlanToCommands.cs turns an ExecutionPlan/compiled Project into ordered catalogue v1 commands (query.levels → wall.create → opening.place → room.create) with SHA-256 operation ids over plan, document, canonical args and inputs (bridge v2); every command parsed by RevitCommandProtocol; every operation accounted as step, precondition or unsupported code; CheckPreconditions against a query.levels reply; Envelope host-placeholder substitution with strict input checks.
- P8-01 (Phase 8): runtimes/ifc/ifc_import.py (+ worker --import-ifc) reads IFC storeys/walls/doors/windows/spaces into existing BIM-IR with placement composition and units, unmapped list with reasons and cascades, range/size refusals, provenance always ifc-import/observed; deterministic validated IFC4 exporter; export→import→export stable.
TESTEADO AUTOMÁTICAMENTE (backend):
- P1-02 tests/revit-commands/ElementQuery.cs: red 41581362575, green 41582425052; review fixes red 41583999465, green 41584475032 (2 failed attempts, both the core vendor-boundary static gate). native-build compiles the executor.
- P3-02 tests/plan-to-commands: red 41581285936, green 41582696545 (1 attempt CS0136); review fixes red 41585441293, green 41586824187.
- P8-01 tests/ifc/test_import.py (23 tests): red 41581606289, green 41582301741; review fixes red 41587908355, green 41588411915 (ifc + ifc-portable on 3 OS).
- The merged integration head runs CI again before main moves.
VERIFICADO EN REVIT NATIVO: nothing new.
BLOQUEADO / POR QUÉ: query.elements/query.inspect need an owner-attended Revit session on a test copy to move past compiled for the collector.
DECISIONES QUE NECESITO DE VOS: none new. No new dependencies, no schema change.
BENCHMARKS / NÚMEROS REALES: none.
GAPS CONOCIDOS:
- Query: Revit spec id shapes and level resolution unverified in Revit; level-only scans walk all model elements; no pagination cursor.
- Bridge: catalogue v1 cannot create levels (preconditions only); created[0] host assumption and opening insertion point unverified; opening sizes from family types; updates/deletes/floors not translated; no runner yet (new P3-03).
- IFC import: only own/worker IFC tested; horizontally extruded openings (Revit/ArchiCAD exports) unmapped; layered/clipped/curved walls unmapped; provenance not carried through IFC.
ARCHIVOS CAMBIADOS: core/Bim.Core/RevitCommandsQuery.cs, RevitCommandsCatalogue.cs, PlanToCommands.cs; runtimes/revit-native/CommandsExecutorQuery.cs, Mirror.cs; runtimes/ifc/ifc_import.py, worker.py; tests/revit-commands/**, tests/plan-to-commands/**, tests/mcp/Program.cs, tests/audit/Program.cs, tests/ifc/test_import.py; scripts/verify.ps1; docs/contracts/module-contracts.md, docs/ifc.md; tasks.md; docs/features.json; docs/agent/ATTEMPTS.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); python -m unittest discover -s tests/ifc -p 'test_*.py' (approved ifcopenshell venv); python runtimes/ifc/ifc_import.py import MODEL.ifc.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: new batch P1-05 modify/delete under the copies guard, P3-03 plan runner, P3-04 localized validation; then P2-03, P4-02, P5-03, P6-02, P7-02, P9-02, P10-02.
```
