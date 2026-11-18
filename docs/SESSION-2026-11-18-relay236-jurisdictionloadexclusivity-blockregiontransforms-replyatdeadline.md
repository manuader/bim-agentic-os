# Relay236: JurisdictionLoad exclusivity and equality, cad-brief block regions through transformed inserts, reply-at-deadline race pinned

Relay236 (cloud scheduled, 2026-11-18 18:52Z) took the FREE LOCK, picked the oldest-touched phases 5 and 6 (relay229) and 1 (relay232), got three workers green, ran three independent read-only reviews (0 blockers; should-fixes closed on the task branches) and integrated through one integration head re-run in CI before main moved. CI is the only oracle (no local .NET SDK). The heartbeat went stale during the review-fix CI waits and relay237 correctly backed off at 20:52Z.

```
IMPLEMENTADO:
- P5-26 (Phase 5): JurisdictionLoad is a non-positional record (explicit constructor and Deconstruct, same public surface) whose constructor and init accessors throw ArgumentException (ParamName = assigned member) unless exactly one of Registry / Refusal is set; Equals/GetHashCode = (Registry by reference, Refusal by value, LoaderError by reference). Defect fixed: neither/both loads and equal-refusal copies comparing equal to the original were representable.
- P6-26 (Phase 6): tests only — cad-brief --include-block-regions through a nested INSERT ([N1,N2]), an asymmetric 3×2 MINSERT cell ([M1[0,2]]) and a mirrored -1.5 X-scale insert ([X1], positive area), CLI equal to in-process, none without the flag.
- P1-31 (Phase 1): tests only — RevitDeadlineTransport keeps a reply line completing exactly at the cut read / uncut read / cut connect / deadline and yields the timeout code one tick later (full bytes pinned); the keep-at-timeout rule of RevitCommandService.Send is modelled by the fake, not unit-tested (→ P1-33).
- Board: P5-26, P6-26, P1-31 ✅; new P1-33, P5-27, P6-28. Progress 246/265 (92.8 %). Contract (JurisdictionLoad) and features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P5-26: red 44374972933, green 44378411572, review green 44393314466. 0 failed attempts. Scope widened by one P5-25 assertion (with {Refusal=null} now throws).
- P6-26: probe red 44374890155 (mirrored extent 34000..40000 observed), green 44378737924, review green 44383201381. 0 failed attempts.
- P1-31: probe red 44375009588 (22 pages sent, not 23), green 44377894371, review green 44388204768. 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P5-26: JurisdictionRegistry compares by reference, so two loads of the same valid directory are unequal → P5-27; a with-copy cannot switch success/refusal (documented).
- P6-26: rotated inserts, rotated MINSERT, non-zero block base points and OCS mirrors not covered → P6-28.
- P1-31: Send's cancel-race branch (line arriving after the timed wait gives up, before cancel) and cutReads:false last-page late reply unpinned → P1-33.
ARCHIVOS CAMBIADOS: core/Bim.Core/JurisdictionRegistry.cs, tests/Bim.Tests/Program.cs; tests/cli/CadBriefBlockRegionTests.cs; tests/cli/RevitCommandDeadlineTests.cs; tasks.md, docs/features.json, docs/contracts/module-contracts.md, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P2-31, P7-24, P7-25, P1-32 (relay232); then P3-22, P3-25, P4-25, P10-27, P10-29 (relay233); then P5-22, P5-27, P6-27, P6-28, P1-33. McpServer.cs edits serialize (P5-22, P10-27); csproj edits serialize (P4-25). Owner: ADR0010/ADR0011; one -Catalogue run.
```
