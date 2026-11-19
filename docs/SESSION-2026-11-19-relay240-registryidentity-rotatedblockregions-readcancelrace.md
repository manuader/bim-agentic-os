# Relay240: JurisdictionRegistry reference identity, rotated block regions, pipe read cancel race

Relay240 (cloud scheduled, 2026-11-19 04:53Z) took the FREE LOCK, picked the oldest-touched phases 5, 6 and 1 (relay236), got three workers green, ran three independent read-only reviews (0 blockers; should-fixes on all three closed with tests) and integrated through one integration head re-run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh). Workers refreshed the LOCK heartbeat during CI waits.

```
IMPLEMENTADO:
- P5-27 (Phase 5): JurisdictionRegistry implements IEquatable<JurisdictionRegistry> with reference identity (ReferenceEquals, RuntimeHelpers.GetHashCode, Equals(object) [NotNullWhen(true)]); documented in the class comment. Behaviour equals the former default; the contract is now explicit and pinned. JurisdictionLoad equality follows it.
- P6-28 (Phase 6): tests/cli/CadBriefBlockRegionTests.cs RunRotated — 30° INSERT, 60° MINSERT cell M2[2,1], block base point (1000,500), rotated 2×1.5 non-uniform scale, label outside the rotated rectangle. Test-only pin: no defect (DxfExploder already composes insertion ∘ rotate ∘ scale ∘ translate(−base)).
- P1-33 (Phase 1): RevitCommandsService.cs: internal ReadReplyLine(readLine, readTimeout, afterWaitGivesUp) + InternalsVisibleTo("Bim.Cli.Tests"); SendMeasured reads only through it (logic moved verbatim). Late-but-before-cancel line kept; cutReads:false late last page kept, late non-final page → next page refused query_all_deadline_exceeded.
- Board: P5-27, P6-28, P1-33 ✅; new P5-28, P6-29, P1-35. Progress 255/274 (93.1 %). Contracts and features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P5-27: red 44462463899 (IEquatable not declared), green 44463781237, review green 44467352341. 0 failed attempts (1 red-phase test fix: CS8602 under --warnaserror).
- P6-28: probe green 44461758841 (no defect), review green 44470380434 (non-uniform scale order + Despensa inverse-transform self-check). 0 failed attempts.
- P1-33: red 44461814408 (ReadReplyLine missing), green 44464505462, review green 44472501166 (internal seam, cancelled-read pin, source check). 0 failed attempts (1 test-side expected-bytes fix).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: P6-28 expected centres Comedor (61232.0508, 1866.0254), Oficina (-1897.1143, 35714.1016), Lavadero (2500, 61750), Garaje (23017.6270, 64773.3168), areas 8 / 2 / 7.5 / 22.5 m2.
GAPS CONOCIDOS:
- P5-27: no content-equality helper; the twice-read CLI bytes pin is not reachable from tests/Bim.Tests (→ P5-28).
- P6-28: MINSERT base point, rotated insert inside a nested chain, OCS mirror with rotation, MINSERT spacing under non-unit scale, bulge outlines (→ P6-29).
- P1-33: a line completing inside Cancel() itself and late-fault observation are not deterministic-tested (→ P1-35); real pipe timing only through existing tests.
ARCHIVOS CAMBIADOS: core/Bim.Core/JurisdictionRegistry.cs, tests/Bim.Tests/Program.cs; tests/cli/CadBriefBlockRegionTests.cs; core/Bim.Core/RevitCommandsService.cs, tests/cli/RevitCommandDeadlineTests.cs; tasks.md, docs/features.json, docs/contracts/module-contracts.md, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P7-24, P7-26, P1-34, P2-32 (relay238); then P3-22, P3-26, P4-26, P10-27, P10-30 (relay239); then P5-22, P5-28, P6-27, P6-29, P1-35. GenerateTypologyCommand.cs edits serialize (P3-26, P4-26, P10-30); McpServer.cs edits serialize (P5-22, P10-27).
```
