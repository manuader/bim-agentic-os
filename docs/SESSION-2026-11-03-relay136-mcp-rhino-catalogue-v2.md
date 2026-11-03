# Relay136: MCP installer, Rhino .3dm adapter and Revit catalogue v2

This was a cloud scheduled relay that started on 2026-11-02 at 22:52Z. GitHub CI was the only oracle, because there is no local .NET SDK and PyPI is blocked here. It took three phases that had not been touched since the roadmap was loaded (1, 8 and 9). Each task went red in CI, then green, then through an independent read-only review. Every should-fix item from the reviews was closed with a test and went green again.

```
IMPLEMENTADO:
- P1-03 (Phase 1): scripts/install-mcp.ps1 + docs/mcp-setup.md. One command registers the repo MCP server in claude_desktop_config.json. It makes a byte-exact backup, writes atomically, and a second run changes nothing. It keeps every other key and server and refuses invalid JSON, duplicate keys and symlinked configs. It finds the Windows MSIX config location. -DryRun and -Uninstall are supported.
- P8-02 (Phase 8): adapters/rhino, which converts BIM-IR JSON to .3dm and back with rhino3dm 8.17.0 (MIT) without Rhino. Ids become object ids, and geometry is read back from the Rhino geometry. It handles metres and millimetres (with Core's mm normalisation), and export→import→export is stable. It refuses foreign, moved, imprecise and malformed input with one JSON error.
- P9-01 (Phase 9): Revit catalogue v2: query.roof_types, roof.create_footprint, stair.create, curtain_wall.create, floor.create_opening. They use the same envelope, copies guard and operation_id receipts as v1. Readback is independent: footprint and area, slope through UnitUtils plus ROOF_SLOPE, a complete riser count, the run path, and panels, mullions and railings in `created`. v1 is unchanged.
TESTEADO AUTOMÁTICAMENTE (backend):
- tests/integration/test-install-mcp.ps1: red 41549610510, green 41550944034. Ubuntu and Windows.
- tests/rhino: 60 tests on the real rhino3dm, red 41548352211, green 41548519960 (rhino job, Linux).
- tests/revit-commands/CatalogueV2.cs: about 190 checks, red 41546933276, green 41547173763. The native-build job compiles the executor against Revit 2025 reference assemblies.
- The merged integration head ran CI again before main moved.
VERIFICADO EN REVIT NATIVO: nothing new.
BLOQUEADO / POR QUÉ: C04/P1-04 catalogue re-run and the N21 third run need the owner's Revit session. Catalogue v2 needs an owner-attended smoke run as well.
DECISIONES QUE NECESITO DE VOS: none. rhino3dm 8.17.0 (MIT) was added under the pre-approved permissive-licence rule. It is CI-only, in an isolated venv.
BENCHMARKS / NÚMEROS REALES: none measured.
GAPS CONOCIDOS:
- Catalogue v2 is not exposed through MCP yet. That is the new task P9-05.
- These Revit behaviours are unverified: a StairsEditScope inside the transaction group, the curtain grid U/V orientation, ROOF_SLOPE on hip roofs, and GetAssociatedRailings.
- stair.create makes one straight run with the default type.
- The Rhino adapter has never been opened in Rhino. It has no arcs or openings, because the Core codec has none. Documentation items are written as origin markers, .3dm bytes are not reproducible, and its tests run on Linux only.
- The MCP installer has not been run against the owner's real Claude Desktop.
ARCHIVOS CAMBIADOS: scripts/install-mcp.ps1, docs/mcp-setup.md, tests/integration/test-install-mcp.ps1, scripts/verify.ps1 (wiring); adapters/rhino/**, tests/rhino/**, requirements/rhino.txt, .github/workflows/ci.yml (rhino job); core/Bim.Core/RevitCommands*.cs, runtimes/revit-native/Commands*.cs, tests/revit-commands/**; tasks.md; docs/features.json.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); python -m venv .tools/rhino && .tools/rhino/bin/python -m pip install --only-binary=:all: -r requirements/rhino.txt && .tools/rhino/bin/python -I tests/rhino/run.py; owner: pwsh -NoProfile -File scripts/install-mcp.ps1 -Build.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: P4-01 view commands (Phase 4 untouched), P9-05 MCP exposure of catalogue v2, P3-02 compiler→catalogue bridge, P5-02 CABA rule pack.
```
