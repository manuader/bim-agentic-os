# Relay137: Revit view commands, DXF reader and MCP exposure of the full catalogue

This cloud scheduled relay started on 2026-11-03 at 00:53Z. GitHub CI was the only oracle (no local .NET SDK). It took three tasks from distinct phases: Phase 4 (never touched before), Phase 6 and Phase 9. Each task went red in CI, then green, then through an independent read-only review; every finding the controller sent back was closed with a test, red then green again. No failed attempts were spent against the three-per-test budget.

```
IMPLEMENTADO:
- P4-01 (Phase 4): Revit view commands query.view_family_types, query.view_templates, view.create_plan, view.create_section, view.create_3d. Same envelope, copies-only guard, operation_id receipts and readback-before-receipt as catalogue v1/v2. Validators reject non-finite and degenerate geometry, scale together with an explicit or default view template, and case-insensitive duplicate names (including templates). Readback checks type, family type, level, name, scale, template, section frame/crop/far clip and 3D section box; any mismatch rolls back. Owner probe tests/integration/native-views-probe.ps1 dumps requested vs observed values.
- P6-01 (Phase 6): adapters/cad/Bim.Cad, a BCL-only ASCII DXF reader into a provenance-tagged drawing model (file, handle, layer, type, line, insert chain, paper space, invisible), $INSUNITS, case-insensitive layers, LINE/ARC/CIRCLE/LWPOLYLINE (bulges)/TEXT/MTEXT/INSERT, OCS (0,0,-1), bounded block explode (MaxDepth, MaxEntities, MaxWork), wall-pair detection with confidence using an offset sweep and MaxSegments/MaxPairs/MaxComparisons limits, canonical JSON. Stable error codes for binary and malformed input.
- P9-05 (Phase 9): MCP tools/list is built from RevitCommandCatalogue.All (v1, v2 and now the view commands). Construction fails closed on invalid or duplicate tool names. Each Revit tool carries _meta bim-agentic-os/permission_level from PermissionPolicy and readOnly/destructive/idempotent hints. The copies-only guard is unchanged.
TESTEADO AUTOMÁTICAMENTE (backend):
- P4-01 tests/revit-commands/CatalogueViews.cs: red 41556783874, green 41557700949; review fixes red 41560648642, green 41560983214. native-build compiles the executor against Revit 2025 reference assemblies.
- P6-01 tests/cad (3 hand-written fixtures, hand-computed transforms/bulges/OCS, 400-case seeded fuzz, nesting bombs, 20k-line sweep): red 41556963237, green 41557712107; review fixes red 41559661537, green 41559877398. Ubuntu and Windows.
- P9-05 tests/mcp, tests/audit, tests/revit-commands: red 41556448758, green 41557276169; review fixes red 41562086309, green 41562897314.
- The merged integration head ran CI again before main moved.
VERIFICADO EN REVIT NATIVO: nothing new.
BLOQUEADO / POR QUÉ: view commands, catalogue v2 and C04/P1-04 need an owner-attended Revit session.
DECISIONES QUE NECESITO DE VOS: none. No new dependencies.
BENCHMARKS / NÚMEROS REALES: none measured (only CI wall-clock limits in tests).
GAPS CONOCIDOS:
- View commands: CreateSection keeping the passed box/far clip and SetSectionBox exact round-trip are assumptions; if Revit pads them every section/boxed 3D call rolls back (fails closed). The section marker is not listed in created. Section faces left of start→end by convention.
- MCP: permission levels are labels; per-actor grants and audit (P10-03) are not enforced in MCP; writes are allowed under any enabled root directory. The forwarded-command check has sample args for v1/v2 only, not view.*.
- DXF: hand-written fixtures only; no binary DXF, DWG, POLYLINE/VERTEX, SPLINE, ELLIPSE, HATCH, DIMENSION; no arc walls or collinear merge; not yet mapped to BIM-IR.
ARCHIVOS CAMBIADOS: core/Bim.Core/RevitCommandsViews.cs, core/Bim.Core/RevitCommandsCatalogue.cs, runtimes/revit-native/Commands*Views.cs, CommandsExecutor.cs, CommandsExecutorV2.cs, tests/revit-commands/**, tests/integration/native-views-probe.ps1, tests/integration/README.md; adapters/cad/**, tests/cad/**, scripts/verify.ps1; interfaces/mcp/McpServer.cs, tests/mcp/**, tests/audit/**, docs/contracts/module-contracts.md; tasks.md; docs/features.json.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK). Owner (Revit, after an -Catalogue session gives the pipe/document): ./tests/integration/native-views-probe.ps1 -DocumentPath <path> -DocumentIdentity <uniqueId> -PipeName <pipe> -Dotnet <dotnet.exe> -CliDll <Bim.Cli.dll> -EvidenceDirectory <dir>.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: P4-02 dimensions/tags/schedules/sheets, P2-01 Revit→BIM-IR snapshot, P10-01 IDS checks; then P3-02, P5-02, P7-02, P8-01.
```
