# Relay241: generate-prototypes post-write cleanup, client write progress, GuardedWorkProbe hygiene

Relay241 (cloud scheduled, 2026-11-19 07:53Z) took the FREE LOCK, picked the oldest-touched phases 7 and 2 (relay238) and 3 (relay239), got three workers green, ran three independent read-only reviews (0 blockers; should-fixes on all three closed with tests) and integrated through one integration head re-run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh). Workers refreshed the LOCK heartbeat during CI waits.

```
IMPLEMENTADO:
- P7-26 (Phase 7): GenerateCommand.Execute deletes the just-written output when a non-refusal exception follows GltfExporter.WriteNew (probe, SHA-256 and success line in one try; catch deletes and rethrows to Run's typed catch-all). Seam PostWriteStageProbe (null in production). Defect fixed.
- P2-32 (Phase 2): RevitCommandsService [ThreadStatic] WriteProgress seam + WriteRequest (null → same single Write). Measured: Windows client had handed 4096 < 39727 wire bytes at the break → P2-31 deviation closed on Windows; Linux all bytes handed.
- P3-22 (Phase 3): GuardedWorkProbe static hygiene over interfaces/ (literal-blanked + literal-kept counts, planted-evasion self-tests), forced-run input cleanup in finally, forced run whose Run throws leaves the seam null. Test-only.
- Board: P7-26, P2-32, P3-22 ✅; new P7-27, P2-33, P3-27. Progress 258/277 (93.1 %). Contracts and features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P7-26: red 44483529697, green 44485593658, review green 44494099367 (brace-matched static pin, 19 evasion self-tests). 0 failed attempts.
- P2-32: red 44483695474, probes 44484659892 / 44487717843, green 44490022080, review green 44498013754 (occurrence-count pin, TMPDIR restore inside try). 1 failed attempt (Linux in-process pipe client used verify.ps1's long TMPDIR; fixed by TMPDIR=/tmp in the try, prefix checked).
- P3-22: probe red 44483624478 (deliberate negative), green 44485216847, review green 44502109698 (literal-kept count, stderr pin). 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: P2-32 Windows handed 4096 / wire 39727, fake queued 1024 (seam) and 35631 (CLI single Write); Linux handed 39736 / 39736, queued 35640.
GAPS CONOCIDOS:
- P7-26: a failing cleanup delete is silent (→ P7-27); P3-25's static pin is weaker than P7-26's brace-matched one.
- P2-32: macOS not measured; Harness pipe name read by reflection (→ P2-33).
- P3-22: other seams lack the hygiene check (→ P3-27); tests/mcp and GenerateTypologyTests seam users not covered by the cleanup.
ARCHIVOS CAMBIADOS: interfaces/cli/GenerateCommand.cs, tests/cli/GeneratePrototypesTests.cs; core/Bim.Core/RevitCommandsService.cs, tests/mirror-journal/MirrorDriftLoopChecks.cs; tests/cli/InternalErrorDelegationTests.cs; tasks.md, docs/features.json, docs/contracts/module-contracts.md, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P3-26, P4-26, P10-27, P10-30 (relay239; GenerateTypologyCommand.cs edits serialize); then P5-22, P5-28, P6-27, P6-29, P1-34, P1-35 (relay240); then P7-24, P7-27, P2-33, P3-27. McpServer.cs edits serialize (P5-22, P10-27).
```
