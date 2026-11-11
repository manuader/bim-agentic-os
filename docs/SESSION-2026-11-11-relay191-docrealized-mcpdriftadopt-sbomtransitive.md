# Relay191: realized map from a run record, mirror_drift_adopt MCP tool, transitive SBOM attribution

Cloud scheduled relay, started 2026-11-11 13:53Z (LOCK was FREE after relay190). GitHub CI was the only oracle (no local .NET SDK or pwsh). Oldest-touched phases with ready work: 4 and 10 (relay187) and 2 (relay188). Workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (1 blocker, 8 should-fix, nits), every finding closed with tests, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P4-13 (Phase 4): core/Bim.Core/DocRealized.cs — FromRecord(fixture, owner -Catalogue run record, {"doc:<key>"|"element:<id>": step key}, expected document UniqueId) → canonical key→UniqueId map that doc-update --realized reads; journal.read replies without `created` accepted (review blocker); UniqueIds later deleted by a succeeded step refused; document mismatch refused; eager fixture validation; typed realized_* codes; bounded (record 32 MiB, output 8 MiB).
- P2-15 (Phase 2): MCP tool mirror_drift_adopt {store_path, selection, confirm?}: plan only without confirm:true (no lock, no store entry touched), confirm:true byte-equal to the CLI mirror-drift-adopt stdout; the engine moved from the CLI into interfaces/mcp/MirrorDriftAdoptTool.cs (CLI bytes unchanged); label "write" with annotations.
- P10-14 (Phase 10): package sbom attributes every shipped .dll/.exe/.so/.dylib to a direct or transitive NuGet package from each entry project's own obj/project.assets.json (dependency, assemblies, natives, nuspec SPDX licence, package Id@Version per the folder's deps.json); sbom_unattributed / sbom_attribution_ambiguous; first-party = deps.json project entries; real package bytes unchanged.
- Board: P4-13, P2-15, P10-14 ✅; new P4-15 (doc-realized CLI verb), P2-17 (Core read-only adopt preview), P10-15 (manifest↔sbom binary cross-check). Progress 150/168 (89.3 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P4-13 tests/doc-regeneration: red 43033957185, green 43036029825; review red 43044249333, green 43045934229. 1 failed attempt (own test helper compile error).
- P2-15 tests/mcp (+ tests/cli, tests/revit-commands tools/list): red 43033953076, green 43035023695; review green 43048882203. 0 failed attempts.
- P10-14 tests/packaging: red 43033968042 and 43054876471, green 43058280336. 3 failed attempts (selector, test setup order, install-addin fixture deps.json).
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; P4-11 / P1-14 owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. Optional: repository licence (first-party sbom lines stay NOASSERTION).
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P4-13: no CLI verb (→ P4-15); caller supplies step → source assignment; first created UniqueId only; deleted dependents beyond the add-in listing cap not seen; never run on a real -Catalogue record.
- P2-15: "write" label outside P10-03 levels, no actor/audit enforcement; plan-mode store reader duplicates Core's (→ P2-17); the tests/mcp ::error annotation change rides in the red test commit.
- P10-14: synthetic fixtures only (no third-party package ships); include/exclude flags on transitive edges not modelled; first-party detection needs deps.json beside the binary.
ARCHIVOS CAMBIADOS: core/Bim.Core/DocRealized.cs; tests/doc-regeneration/{Realized,Program}.cs; interfaces/mcp/{MirrorDriftAdoptTool,McpServer}.cs; interfaces/cli/{MirrorDriftAdoptCommand.cs,Bim.Cli.csproj}; tests/mcp/{Program.cs,Bim.Mcp.Tests.csproj}; tests/cli/{MirrorDriftAdoptTests.cs,Bim.Cli.Tests.csproj}; tests/revit-commands/{Program.cs,Bim.RevitCommands.Tests.csproj}; scripts/package-common.ps1; tests/packaging/{test-package,test-install-addin}.ps1; board/features/contracts/attempts/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P1-18/P1-20/P1-21, P2-16, P6-15 (relay188; P1-20 and P1-21 share the revit-command flag parser — serialize); then P3-13, P5-15, P7-14/P7-15 (relay190; P7-14 and P7-15 share SkillPackLookup — serialize); then P4-14/P4-15, P2-17, P10-15 (relay191; P4-14 and P1-18 share tests/integration/test-catalogue-session.ps1 — serialize). Owner: ADR0010/ADR0011; one -Catalogue run.
```
