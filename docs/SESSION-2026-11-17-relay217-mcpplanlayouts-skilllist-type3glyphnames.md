# Relay217: plan_layouts MCP tool, skill-list verb, Type3 glyph-list warning

Relay217 (cloud scheduled, 2026-11-17 06:53Z) took the FREE LOCK, picked the oldest-touched phases 7, 5 and 6 (relay214/215), got three workers green, ran three independent read-only reviews, closed every should-fix with tests on the task branches, and integrated through one integration head re-run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh).

```
IMPLEMENTADO:
- P7-20 (Phase 7): read-only MCP tool plan_layouts {brief, alternatives?, seed?, pack?} calling PlanLayoutsCommand.DryPlan; the CLI body moved verbatim into a shared Plan(...) + option validators (CLI bytes unchanged). Text byte-equal to the CLI output file; refusals equal the CLI line.
- P5-21 (Phase 5): read-only CLI verb skill-list [--skills dir] (package-first) listing packs {id, version, kind, stub, source_confidence counts} ordered by id; loader and jurisdiction-sidecar refusals pass through unchanged; reusable List() handler for P5-22.
- P6-22 (Phase 6): Type3 /Differences glyph-list names the reader does not map (afii+5 digits, rejected uni/u forms, embedded AGL subset) give a typed encoding-glyph-unsupported warning once per font per page; slot names stay silent; text bytes unchanged.
- Board: P7-20, P5-21, P6-22 ✅. New: P7-22 (tool internal/refused paths), P5-23 (load once + registry issues + culture), P6-24 (warn only for shown codes). Progress 213/231 (92.2 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P7-20: red 44035035936, green 44038349793; review pins green 44054329853. 1 failed attempt (compile: McpServer.cs also compiled by tests/cli and tests/revit-commands).
- P5-21: red 44034963800, green 44036668745; review red 44041974987, green 44043953028. 0 failed attempts.
- P6-22: red 44034850877, green 44037286821; review red 44047618622, green 44050432124. 0 failed attempts.
- Reviews (independent, read-only): 0 blockers. Should-fixes closed with tests: P5-21 sidecar refusal shape pinned + skills_path trailing separator trimmed; P6-22 ToUnicode/no-CMap message wording pinned literally + 8-name cap and dedup; P7-20 malformed-brief literal + pack_invalid cases. Contract sections and features added at integration; P7-20 forced internal path → P7-22.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P7-20: non-int32 alternatives/seed are -32602 at MCP (CLI gives typed _invalid); tool internal/refused paths untested (→ P7-22); P7-20 widened scope to 4 csproj links and two tools/list pins — P7-21 and P5-22 must serialize behind it.
- P5-21: registry issues not reported, directory loaded twice (→ P5-23); internal-error path static only.
- P6-22: embedded AGL list partial; warning fires at font selection even if affected codes are never shown (→ P6-24).
ARCHIVOS CAMBIADOS: interfaces/mcp/{PlanLayoutsTool.cs,McpServer.cs,Bim.Mcp.csproj}; interfaces/cli/{PlanLayoutsCommand,SkillListCommand,Program}.cs; adapters/cad/Bim.Cad/{PdfEncoding,PdfContent}.cs; tests/mcp/{PlanLayoutsToolTests,Program}.cs, tests/mcp/Bim.Mcp.Tests.csproj; tests/cli/{SkillListTests,Program}.cs, tests/cli/Bim.Cli.Tests.csproj; tests/revit-commands/{Program.cs,Bim.RevitCommands.Tests.csproj}; tests/cad/{PdfType3GlyphListTests,PdfNamedEncodingTests,Program}.cs; docs/contracts/module-contracts.md; board/features/attempts/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P3-20, P10-23/P10-24 (relay215/213); P4-21/P4-22, P1-27, P2-27 (relay216); then P5-22 and P7-21 (both McpServer.cs + tool-list pins + 4 csprojs — serialize), P5-23, P6-23/P6-24, P7-22. Owner: ADR0010/ADR0011; one -Catalogue run.
```
