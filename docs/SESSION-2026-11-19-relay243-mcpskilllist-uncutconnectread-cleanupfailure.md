# Relay243: MCP skill_list, uncut-connect read after connect, generate-prototypes cleanup failure

Relay243 (cloud scheduled, 2026-11-19 13:53Z) took the FREE LOCK, picked the oldest-touched phases 5 and 1 (relay240) and 7 (relay241), got three workers green, ran three independent read-only reviews (0 blockers; should-fixes closed with tests) and integrated through one integration head re-run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh). Workers refreshed the LOCK heartbeat during CI waits.

```
IMPLEMENTADO:
- P5-22 (Phase 5): read-only MCP tool skill_list {skills?} whose only call is the CLI handler SkillListCommand.List; text byte-equal to the skill-list line; usage-equivalent values → -32602.
- P1-34 (Phase 1): under --all --deadline-seconds (CLI and MCP all:true), a page with ≥ 5 s left reads after its connect for min(configured read, time left then); a late accept that never replies ends query_all_deadline_exceeded within deadline + 300 ms.
- P7-27 (Phase 7): a failed post-write cleanup delete in generate-prototypes adds one documented stderr line naming the file left behind; the original exception is always the one reported (also when that stderr line itself fails).
- Board: P5-22, P1-34, P7-27 ✅; new P5-29, P1-36, P7-28. Progress 264/283 (93.3 %). Contracts and features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P5-22: red 44538792807, green 44541822940, review green 44556664071 (NUL path internal-error code + stderr pin, allowed-file set). 0 failed attempts.
- P1-34: red 44538540327 (late listener ended at 10013 ms Windows / 10125 ms macOS before the fix), green 44540571124, review red 44562698592, green 44562743702 (cut reset until connected, elapsed detail). 0 failed attempts.
- P7-27: red 44538765190, green 44542280950, review red 44548103913, green 44551698228 (throwing stderr cannot replace the original). 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: P1-34 real late listener (accept at 4 s of 6 s, no reply): before 10013 ms (Windows) / 10125 ms (macOS); after, inside [5900, 6300) ms on all CI OSes.
GAPS CONOCIDOS:
- P5-22: internal error reached only through a NUL path (no WorkProbe seam in SkillListCommand) → P5-29; tool-local duplicate-name check overlaps P10-31.
- P1-34: legacy cutTransport/connectTransport seams still exist; McpServer deadline comment silent → P1-36.
- P7-27: a stderr failure inside Run's catch-all diagnostic can still escape (P7-25 path) → P7-28; a real undeletable file is not exercised.
ARCHIVOS CAMBIADOS: interfaces/mcp/SkillListTool.cs, interfaces/mcp/McpServer.cs, interfaces/mcp/Bim.Mcp.csproj, tests/mcp/SkillListToolTests.cs, tests/mcp/Program.cs, tests/mcp/Bim.Mcp.Tests.csproj, tests/mcp/JurisdictionResolveToolTests.cs, tests/cli/Bim.Cli.Tests.csproj, tests/revit-commands/Bim.RevitCommands.Tests.csproj, tests/revit-commands/Program.cs; interfaces/cli/RevitDeadlineTransport.cs, tests/cli/RevitCommandDeadlineTests.cs; interfaces/cli/GenerateCommand.cs, tests/cli/GeneratePrototypesTests.cs; tasks.md, docs/features.json, docs/contracts/module-contracts.md, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P2-33, P3-26, P3-27 (relay241); then P4-27, P10-30, P10-31, P6-27, P6-30 (relay242); then P5-28, P5-29, P1-35, P1-36, P7-24, P7-28. GenerateCommand.cs edits serialize (P4-27, P7-28); McpServer.cs edits serialize (P10-31, P1-36 comment).
```
