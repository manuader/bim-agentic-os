# Relay261: MCP generate_typology throwing stderr, unsearchable cwd literals, nested insert elevation

Relay261 (cloud scheduled, 2026-11-21 09:52Z) found the LOCK FREE (relay260 released 09:23Z), took it, and picked one task in each of the oldest-touched phases 3, 10 and 6 (relay256/257 leftovers): P3-32, P10-35, P6-34. Three isolated workers ran TDD against CI (no local .NET SDK; CI is the only oracle), one independent read-only review covered all three branches (all approve, 0 blockers; nits recorded as follow-up tasks), and one integration head ran CI before main moved.

```
IMPLEMENTADO:
- P3-32 (Phase 3): test + contract only — in-process MCP generate_typology with a throwing/disposed stderr on the Document catch-all and InternalRefusal paths returns isError with the typed internal-error text, one stderr attempt, nothing escapes McpServer; PartialWriter/CountingWriter override WriteLine() and Write(char[],int,int); StderrWriteProblems pin replaces the look-behind regex (10 accepted, 17 rejected mutations; also scans GenerateTypologyWrite.cs); contract: exit code authoritative after a partial write.
- P10-35 (Phase 10): test-only — Linux mode-000 cwd literal lines pinned for generate-prototypes, generate-typology, plan-layouts, export-gltf, bcf-export; the late output_io_error (audit-verify --write-anchor) pinned with its measured reason; MCP PathProblem under an unsearchable cwd (8 tools, cwd/parent modes) measured input_access_denied / member_missing / lock_missing, never the generic failure.
- P6-34 (Phase 6): DEFECT FIXED — a nested INSERT's group-30 elevation was dropped when exploding under an enclosing near-±Z insert (1.3e-3 mm off vs ezdxf); DxfInsert carries Elevation/ZAxis (not serialized), DxfExploder carries a block-z → world-XY vector (zero for exact ±Z at every level, existing bytes unchanged). Three-level chain and a 2×3 MINSERT in a chain equal ezdxf 1.4.2 within 1e-6; bulge parity for every D1 cell; named CLI precondition. Contracts: reader warnings excluded from the brief (documented limit) + proposed opt-in for the owner (not implemented).
- Board: P3-32, P10-35, P6-34 ✅; new P3-34, P10-37, P6-36. Progress 315/334 (94.3 %). features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P3-32: red 44892195373, green 44893910872. 0 failed attempts.
- P10-35: probe red 44892086261, green 44893403588. 0 failed attempts.
- P6-34: red 44892111521, green 44893977816. 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged; optional P6-35 skip-the-form choice; new optional: P6-34 reader-warnings opt-in (BriefExtractOptions.IncludeReaderWarnings / --include-reader-warnings), written up in docs/contracts cad-brief section.
BENCHMARKS / NÚMEROS REALES: P6-34 nested-elevation error before the fix 1.318e-3 mm (E1>TM>MA); after, constants within 7e-11 of an independent recomputation.
GAPS CONOCIDOS:
- P3-32: real Bim.Mcp.dll with throwing stderr, disposed-writer attempt count, per-line full-line cut, contract wording check → P3-34.
- P10-35: <tool>_path_invalid unreachable on Linux (macOS PathProblem UnauthorizedAccessException unverified, tests/mcp not on macOS); parent-mode literals; late write on a named verb; doubled label prefix → P10-37.
- P6-34: MINSERT at non-zero elevation in a chain, exact nested under near-Z, Z scale / base z / leaf z / ATTRIB z, contract wording → P6-36.
ARCHIVOS CAMBIADOS: tests/mcp/GenerateTypologyToolTests.cs, tests/cli/GenerateTypologyTests.cs; tests/cli/DeletedCwdPathTests.cs, tests/mcp/DeletedCwdPathToolTests.cs; adapters/cad/Bim.Cad/{DxfModel,DxfReader,DxfExploder}.cs, tests/cad/MinsertEzdxfTests.cs, tests/cad/tools/minsert_ezdxf_centres.py; docs/contracts/module-contracts.md; tasks.md, docs/features.json, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P10-36, P6-35, P7-36 (relay257); then P1-40, P1-41, P5-34, P5-35, P7-37 (relay258); then P2-39, P3-33, P4-33 (relay260); then P3-34, P10-37, P6-36.
```
