# Relay257: MCP lone-surrogate text, form matrix overflow, partial-write contract

Relay257 (cloud scheduled, 2026-11-21 03:52Z) took the FREE LOCK, picked the oldest-touched phases 10, 6 and 7 (relay254), got three workers green, ran one independent read-only review of all three branches (all approve, 0 blockers; nits recorded as follow-up tasks) and integrated through one integration head run in CI before main moved. CI is the only oracle (no local .NET SDK).

```
IMPLEMENTADO:
- P10-34 (Phase 10): DEFECT FIXED — lone-surrogate member names in the JSON-RPC envelope / tools/call params and lone-surrogate method, jsonrpc, id and params.name values gave -32603 internal_error (id null) or were silently ignored when placed first; now typed -32600 / -32602 refusals, id echoed only when unambiguous; exact response bytes pinned.
- P6-33 (Phase 6): DEFECT FIXED — a non-finite form /Matrix × CTM composition silently kept the outer CTM; now operand-error at the Do (form still runs under the outer CTM). Nested form matrices and a skewed Tm x-only rise overflow pinned pending; one SwallowGuard helper with ordered anchors.
- P7-34 (Phase 7): test + contract only — exit code authoritative after a partial stdout write; static no-flush/no-retry pin (23 mutants); CountingWriter overloads; full-line budget cut.
- Board: P10-34, P6-33, P7-34 ✅; new P10-36, P6-35, P7-36. Progress 306/325 (94.2 %). Contracts and features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P10-34: probe red 44863689484, green 44864655365. 0 failed attempts.
- P6-33: red 44863889900, green 44865457578. 0 failed attempts.
- P7-34: red 44863723911, green 44865435745. 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. New optional: P6-35 records skip-the-form as an owner option for a non-finite form matrix (current: run under outer CTM with a warning).
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P10-34: other methods' params names, raw lone surrogate char in the line, _meta, stdio process not exercised; valid surrogate-pair id bytes parsed not pinned → P10-36.
- P6-33: form still runs at the wrong scale (signalled); no CLI pin; malformed /Matrix silently identity; cm vs Do message shapes differ → P6-35.
- P7-34: PartialWriter lacks the overloads; Execute-internal retry not pinned; Forced helper copied → P7-36.
ARCHIVOS CAMBIADOS: interfaces/mcp/McpServer.cs, tests/mcp/RepeatedEnvelopeKeyTests.cs; adapters/cad/Bim.Cad/PdfContent.cs, tests/cad/PdfType3GlyphListTests.cs; tests/cli/GeneratePrototypesTests.cs; docs/contracts/module-contracts.md, tasks.md, docs/features.json, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P1-39, P1-40, P5-33, P5-34, P7-35 (relay255); then P2-38, P3-29, P3-32, P4-32 (relay256); then P10-35, P10-36, P6-34, P6-35, P7-36.
```
