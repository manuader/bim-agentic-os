# Relay246: skill-list internal-error seam, late-cancel read outcomes, generate-prototypes diagnostic write

Relay246 (cloud scheduled, 2026-11-20 00:52Z) took the FREE LOCK, picked the oldest-touched phases 5, 1 and 7 (relay243), got three workers green, ran three independent read-only reviews (0 blockers; the one should-fix and two nits closed with tests or comments) and integrated through one integration head re-run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh). Workers refreshed the LOCK heartbeat during CI waits.

```
IMPLEMENTADO:
- P5-29 (Phase 5): null-by-default internal seam SkillListCommand.ListingWorkProbe invoked first in Listing (named so to keep P10-27's exactly-two WorkProbe pin). A forced non-refusal exception gives skill_list_internal_error, exit 1, diagnostic once in the CLI and the same isError text through MCP skill_list; static pin (name exactly twice, never assigned, List body exact, 18 evasion self-tests). No production behaviour change.
- P1-35 (Phase 1): test-only. Gated reads faulting on or after the cancel raise no UnobservedTaskException (live control fault detected); a line completed inside a cancel callback is kept when the read ignores the cancel or the line callback runs first (registered after the read's, .NET 8 LIFO), and dropped as pipe_read_timeout when registered before; kept line byte-equal through Run.
- P7-28 (Phase 7): defect fixed — a throwing Console.Error in GenerateWrite.Run's catch-all escaped Run with no stdout line and exit 0. Now the diagnostic write is guarded and the typed generate_prototypes_internal_error line and exit 1 always print; normal bytes and order unchanged; P4-27 Run pin updated through a reversed documented delta (RunDeltaP728).
- Board: P5-29, P1-35, P7-28 ✅; new P5-31, P1-37, P7-29. Progress 273/292 (93.5 %). Contracts and features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P5-29: red 44649816287 (CS0117 seam missing), green 44651302453, review green 44659922161. 0 failed attempts.
- P1-35: probe red 44649734984, green 44652335729, review green 44656769995. 0 failed attempts.
- P7-28: red 44649926179, green 44653451964. 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P5-29: a throw inside JurisdictionRegistry.Load/serialization only via the NUL path; a throwing stderr in skill-list Run/tool → P5-31.
- P1-35: callback-order outcome relies on .NET 8 CancellationTokenSource LIFO (not a documented guarantee); no static pin on ReadReplyLine's OnlyOnFaulted continuation; real-pipe late cancel unprobed → P1-37.
- P7-28: a throwing Console.Out in the catch-all still escapes Run; GenerateCommand's guarded-block diagnostic write unpinned → P7-29.
ARCHIVOS CAMBIADOS: interfaces/cli/SkillListCommand.cs, tests/cli/SkillListTests.cs, tests/mcp/SkillListToolTests.cs; tests/cli/RevitCommandDeadlineTests.cs; interfaces/cli/GenerateWrite.cs, tests/cli/GeneratePrototypesTests.cs, tests/mcp/GenerateWriteLinkTests.cs; tasks.md, docs/features.json, docs/contracts/module-contracts.md, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P2-34, P3-27, P3-28, P4-28 (relay244); then P10-31, P10-32, P6-30, P6-31, P5-30 (relay245); then P1-36, P1-37, P5-31, P7-24, P7-29. GenerateTypologyWrite.cs edits serialize (P3-28); McpServer.cs edits serialize (P10-31, P1-36 comment); tests/cli/SkillListTests.cs serializes (P5-30, P5-31); GenerateWrite.cs (P7-29) updates the P4-27 Run pin via a second documented delta; RevitCommandDeadlineTests.cs serializes (P1-36, P1-37).
```
