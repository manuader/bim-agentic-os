# Relay262: MCP raw lone-surrogate line, malformed form /Matrix, generate-prototypes Execute no-retry

Relay262 (cloud scheduled, 2026-11-21 11:52Z) found the LOCK FREE (relay261 released 11:22Z), took it, and picked one task in each of the oldest-touched phases 10, 6 and 7 (relay257 leftovers): P10-36, P6-35, P7-36. Three isolated workers ran TDD against CI (no local .NET SDK; CI is the only oracle), one independent read-only review covered all three branches (all approve, 0 blockers; nits recorded as follow-up tasks), and one integration head ran CI before main moved.

```
IMPLEMENTADO:
- P10-36 (Phase 10): DEFECT FIXED — a raw (unescaped) lone surrogate anywhere in the line gave -32603 internal_error id null; now McpServer.Handle refuses it before parsing with -32700 `parse_error: line is not valid text`. A lone-surrogate params member name on initialize/ping/tools/list was silently ignored; now -32602 `invalid_params: params member name is not valid text` with the id. _meta lone surrogates pinned as accepted (never read). Valid surrogate-pair ids echoed exactly. The real Bim.Mcp.dll over stdio reproduces the P10-34 refusals byte-for-byte (stdin replaces invalid UTF-8, so a raw lone surrogate cannot reach Handle that way).
- P6-35 (Phase 6): DEFECT FIXED — a malformed form /Matrix (≠6 entries, non-numeric entry, not an array; 9 shapes) was silently the identity; now operand-error `Do /<name> expects a /Matrix of 6 numbers; the identity matrix is used`. cm vs Do message shapes pinned. cad-brief CLI bytes equal to in-process; reader warnings are not part of the brief (pinned, same limit as P6-34). Contract records run-under-outer-CTM and the skip-the-form owner option.
- P7-36 (Phase 7): test-only — PartialWriter Write(char[],int,int)/WriteLine() overrides with self-tests, one ForcedRun helper for sections 12/13, static ExecuteWriteProblems pin over GenerateWrite.Execute (16 mutants rejected).
- Board: P10-36, P6-35, P7-36 ✅; new P10-38, P6-37, P7-38. Progress 318/337 (94.4 %). features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P10-36: probe red 44902605369, green 44904847700. 0 failed attempts.
- P6-35: probe red 44901513922, green 44904703311. 0 failed attempts.
- P7-36: red 44901470230, green 44902382714. 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged; optional P6-35 skip-the-form choice (now documented in docs/contracts cad-brief section); optional reader-warnings opt-in (P6-34) would also carry PDF operand-errors into the brief.
BENCHMARKS / NÚMEROS REALES: P6-35 brief digests overflow DE7127CCF6EAD8BF, malformed 016B3C6BA998C1D6 (Linux and Windows).
GAPS CONOCIDOS:
- P10-36: repeated params members of initialize/ping/tools/list still ignored; stdio launch after a missing dll; helper naming → P10-38.
- P6-35: non-finite numeric /Matrix entries wording, warn-once per form, test document-shape drift vs CadCommand, macOS digest → P6-37.
- P7-36: one-copy pin self-test, writes via a helper outside Execute, pin brittleness → P7-38.
ARCHIVOS CAMBIADOS: interfaces/mcp/McpServer.cs, tests/mcp/RepeatedEnvelopeKeyTests.cs; adapters/cad/Bim.Cad/PdfContent.cs, tests/cad/PdfType3GlyphListTests.cs; tests/cli/GeneratePrototypesTests.cs; docs/contracts/module-contracts.md; tasks.md, docs/features.json, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P1-40, P1-41, P5-34, P5-35, P7-37 (relay258); then P2-39, P3-33, P4-33 (relay260); then P3-34, P10-37, P6-36 (relay261); then P10-38, P6-37, P7-38.
```
