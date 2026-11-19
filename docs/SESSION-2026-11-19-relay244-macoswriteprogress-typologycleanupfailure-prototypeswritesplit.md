# Relay244: macOS write progress, generate-typology cleanup failure, generate-prototypes write split

Relay244 (cloud scheduled, 2026-11-19 17:52Z) took the FREE LOCK, picked the oldest-touched phases 2 and 3 (relay241) and 4 (relay242), got three workers green, ran three independent read-only reviews (0 blockers; should-fixes closed with tests) and integrated through one integration head re-run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh). Workers refreshed the LOCK heartbeat during CI waits.

```
IMPLEMENTADO:
- P2-33 (Phase 2): the -BreakAfterBytes 4096 write-progress measurement runs on macOS (new CI step, mirror-journal --write-progress-only) through the read-only Harness.Pipe; handed facts pinned as relations to the envelope on all three OSes; CLI process line byte-equal to the seam line.
- P3-26 (Phase 3): a failed generate-typology post-write cleanup delete adds `generate-typology: could not delete partial output: <output>` on stderr; the original exception is always the one reported; P4-26 verbatim pin kept via a documented reversed delta.
- P4-27 (Phase 4): GenerateCommand split; the generate-prototypes write path lives in CLI-only GenerateWrite.cs (SHA pins); the MCP server compiles no GltfExporter.WriteNew caller and Bim.Mcp.dll has no WriteNew MemberRef.
- Board: P2-33, P3-26, P4-27 ✅; new P2-34, P3-28, P4-28; P7-28 retargeted to GenerateWrite.cs. Progress 267/286 (93.4 %). Contracts and features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P2-33: deliberate macOS probe red 44582611952, green 44586113882, review green 44591413868 (recv/send buffer conjunct dropped, prefix warning). 0 failed attempts.
- P3-26: red 44582683717, green 44585805948, review mutation red 44596502261, green 44599122585. 0 failed attempts.
- P4-27: red 44582754418, new-section red probe 44603886602, green 44585131245, final 44603936541. 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. Delete the leftover probe branch relay244-P4-27-red (the cloud proxy refused branch deletion).
BENCHMARKS / NÚMEROS REALES: macOS mid-args break: client handed 12288 of 39757 wire bytes (4096 read + fake InBufferSize 8192) when the fake closed.
GAPS CONOCIDOS:
- P2-33: the fake does not measure queued bytes on macOS (-1 pinned) → P2-34; the macOS step sits in the ifc-portable job.
- P3-26: a stderr failure inside Run's catch-all diagnostic can still escape; ForbiddenUse regex does not name the new seam (metadata covers it) → P3-28.
- P4-27: Bim.Mcp still has a Bim.Gltf ProjectReference → P4-28. P7-27 seam and PartialOutputLeft moved with Execute (deviation).
ARCHIVOS CAMBIADOS: tests/mirror-journal/MirrorDriftLoopChecks.cs, tests/mirror-journal/Program.cs, .github/workflows/ci.yml; interfaces/cli/GenerateTypologyWrite.cs, tests/cli/GenerateTypologyTests.cs, tests/mcp/GenerateTypologyWriteLinkTests.cs; interfaces/cli/GenerateCommand.cs, interfaces/cli/GenerateWrite.cs, tests/mcp/GenerateWriteLinkTests.cs, tests/mcp/Program.cs, tests/mcp/GeneratePrototypesToolTests.cs, tests/mcp/GltfExportLinkTests.cs, tests/mcp/PlanLayoutsWriteLinkTests.cs, tests/cli/GeneratePrototypesTests.cs; tasks.md, docs/features.json, docs/contracts/module-contracts.md, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P10-30, P10-31, P6-27, P6-30 (relay242); then P5-28, P5-29, P1-35, P1-36, P7-24, P7-28 (relay243); then P2-34, P3-27, P3-28, P4-28. GenerateTypologyWrite.cs edits serialize (P3-28; P10-30 touches GenerateTypologyCommand.cs); GenerateWrite.cs edits serialize (P7-28); McpServer.cs edits serialize (P10-31, P1-36 comment); tests/mcp/GenerateTypologyWriteLinkTests.cs serialize (P3-28, P4-28 if touched).
```
