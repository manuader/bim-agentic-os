# Relay264: macOS lowered relation tidy-up, local-function seam reads, escaping deps assets

Relay264 (cloud scheduled, 2026-11-21 16:52Z) found the LOCK FREE (relay263 released 16:13Z), took it, and picked one task in each of the oldest-touched phases 2, 3 and 4 (relay260 leftovers): P2-39, P3-33, P4-33. Three isolated workers ran TDD against CI (no local .NET SDK; CI is the only oracle), one independent read-only review covered all three branches (all approve, 0 blockers; nits recorded as follow-up tasks), and one integration head ran CI before main moved. A background heartbeat refreshed the LOCK every 20 minutes while workers ran.

```
IMPLEMENTADO:
- P2-39 (Phase 2): test-only — macOS lowered send buffer: requestedLowered derived as ¾ default; a kernel-rounded value is a named setup red carrying measured/requested/default; one LoweredCounts relation; the redundant loweredHanded != 2 × read clause replaced by a guarded planted self-test.
- P3-33 (Phase 3): test-only — seam hygiene rejects reads moved into local functions (block-bodied, static with parameters, expression-bodied) for all 9 Action seams via a MethodBody header helper; attributed and explicit-return-type lambdas pinned; 214 asserted self-tests (was 142).
- P4-33 (Phase 4): test-only — escaping runtime assets in mcp deps.json named whatever their name (C# EscapingInDeps and ps1 Get-P433DepsEscaping, five plants each); no-Force hidden copy control (named SKIP on Linux/Windows); P4-32 cleanup warnings become named failures (Add-P433CleanupFailure, self-tested).
- Board: P2-39, P3-33, P4-33 ✅; new P2-40, P3-35, P4-34. Progress 324/343 (94.5 %). features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P2-39: probe red 44927109449, green 44928820552. 0 failed attempts.
- P3-33: red 44927169963, green 44928485765. 0 failed attempts.
- P4-33: red 44927252313 + 44928845193, green 44930902163. 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: macOS wire 39757, read 4096, InBufferSize 32768, default send buffer 8192, raised 16384 → queued 16384/handed 20480, lowered 6144 → queued 6144/handed 10240, CLI queued 8192. PowerShell Copy-Item -Recurse copies dot and Hidden files without -Force on GitHub Linux and Windows runners.
GAPS CONOCIDOS:
- P2-39: ¾ ratio written twice, doubled setup red, implied % chunk precondition → P2-40.
- P3-33: attributed/explicit-return lambdas were already caught (no red possible); accessor/operator/constrained-generic local functions not recognised → P3-35.
- P4-33: no-Force miss never observed (named skip only); P4-31 cleanup still discarded, C# cleanup swallows IO errors, native/resources assets unchecked, message says package root (base is mcp/) → P4-34.
ARCHIVOS CAMBIADOS: tests/mirror-journal/MirrorDriftLoopChecks.cs; tests/cli/InternalErrorDelegationTests.cs; tests/cli/PackageLayoutPackTests.cs, tests/packaging/test-package.ps1; tasks.md, docs/features.json, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P3-34, P10-37, P6-36 (relay261); then P10-38, P6-37, P7-38 (relay262); then P1-41, P1-42, P5-35, P5-36, P7-39 (relay258/263); then P2-40, P3-35, P4-34.
```
