# Relay255: deadline transport tidy, selection twice-read, packaged MCP catalogue

Relay255 (cloud scheduled, 2026-11-20 23:53Z) took the FREE LOCK, picked the oldest-touched phases 1, 5 and 7 (relay252), got three workers green, ran one independent read-only review of all three branches (all approve, 0 blockers; nits recorded as follow-up tasks) and integrated through one integration head run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh).

```
IMPLEMENTADO:
- P1-38 (Phase 1): interfaces/cli/Program.cs passes null as RevitDeadlineTransport's configured transport; RevitDeadlineTransport.cs takes a nullable transport and throws ArgumentNullException only when cutReads is false. DEFECT FIXED (tidy): a never-called transport was passed. PipeTransportWithReadTimeout kept (production callers RevitCommandOptions.cs, McpServer.cs pinned by a static scan). Legacy test adapter: named Check LegacyUnmodelled instead of throwing. StripComments handles @$ and raw strings (mutation self-test).
- P5-32 (Phase 5): test-only. jurisdiction-resolve selection and --allow-stub under --lock twice in process, twice as a real process, and MCP jurisdiction_resolve twice in-process and packaged; literal pins; json_invalid detail pinned on .NET 8.
- P7-33 (Phase 7): test-only. Packaged infeasible sha256 pinned CLI == MCP; generate_typology/plan_layouts packaged fallback parity; stray skills/ ignored; 79-name tools/list catalogue pinned; batch vs awaited handshake byte-equal; cleanup warning self-test.
- Board: P1-38, P5-32, P7-33 ✅; new P1-40, P5-34, P7-35. Progress 300/319 (94 %). Contracts and features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P1-38: probe red 44840568353, green 44842650745. 0 failed attempts.
- P5-32: red 44841766427 (after compile slip 44840534874), green 44842913051. 0 failed attempts.
- P7-33: red 44841962105 (after parse slip 44840632934), green 44844244266. 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: none.
GAPS CONOCIDOS:
- P1-38: caller scan walks the working tree (not git ls-files); positional Program.cs pin; McpServer still passes a never-called transport when cutReads → P1-40. Interpolation holes in StripComments → P1-39.
- P5-32: conflict/ambiguous/drifted-lock not covered; MCP Cli() helper ignores stderr; P5-30 json_invalid StartsWith pin → P5-34.
- P7-33: typology/layout document sha not pinned; earlier cleanups still silent; self-test would fail under root; catalogue pin moves with every new tool → P7-35.
ARCHIVOS CAMBIADOS: interfaces/cli/Program.cs, interfaces/cli/RevitDeadlineTransport.cs, tests/cli/RevitCommandDeadlineTests.cs; tests/cli/SkillListTests.cs, tests/mcp/TwiceReadToolTests.cs; tests/packaging/test-package.ps1; tasks.md, docs/features.json, docs/contracts/module-contracts.md, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P2-37, P3-29, P3-31, P4-31 (relay253); then P10-34, P10-35, P6-33, P6-34, P7-34 (relay254); then P1-39, P1-40, P5-33, P5-34, P7-35.
```
