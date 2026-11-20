# Relay247: macOS queued bytes, remaining seam hygiene, MCP Bim.Gltf references

Relay247 (cloud scheduled, 2026-11-20 03:52Z) took the FREE LOCK, picked the oldest-touched phases 2, 3 and 4 (relay244), got three workers green, ran three independent read-only reviews (0 blockers; every should-fix closed with tests or comments) and integrated through one integration head re-run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh). Workers refreshed the LOCK heartbeat during CI waits.

```
IMPLEMENTADO:
- P2-34 (Phase 2): the fake's FakePipeQueue now measures queued bytes on macOS (FIONREAD via Socket.Available on a non-owning SafeSocketHandle; a direct DllImport of variadic ioctl is unsafe on Apple arm64). macOS pin: seamQueued == InBufferSize, cliQueued == seamQueued, cliQueued < wire − 4096; Linux/Windows unchanged.
- P3-27 (Phase 3): test-only. SeamHygiene applies the P3-22 static hygiene to UnguardedStageProbe, UnguardedWorkProbe, PostWriteProbe, PostWriteStageProbe and GenerationWeightsProbe; LexerGuard closes the CodeOf raw/@$ comment-mislex holes (also for GuardedWorkProbe). 82 self-tests.
- P4-28 (Phase 4): the Release Bim.Mcp.dll references no Bim.Gltf metadata at all (Bim.Cli.dll control). Removing the ProjectReference builds and keeps all MCP pins but breaks the P10-18 package-layout pin (mcp/Bim.Gltf.dll) → reference kept, guarded by the test, removal filed as P4-29.
- Board: P2-34, P3-27, P4-28 ✅; new P2-35, P3-29, P4-29. Progress 276/295 (93.6 %). Contracts and features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P2-34: probe red 44671009756 (macOS queued 8192), green 44673071291, review green 44678815185. 0 failed attempts.
- P3-27: probe red 44671203658, green 44673564292, review red 44681712305, green 44683698783. 0 failed attempts.
- P4-28: red 44671076645, removal attempt 44673074883 (P10-18 pin), review compile error 44686779083, green 44688933750. 2 failed attempts (distinct hypotheses).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: macOS CI runner: fake receive buffer 8192, default unix send buffer 8192, queued 8192 at a 4096-byte break of a 39757-byte request.
GAPS CONOCIDOS:
- P2-34: queued 8192 equals both InBufferSize and the default send buffer; cause not isolated; fixed 200 ms wait → P2-35.
- P3-27: PostWriteDeleteProbe, PostWriteRemovalProbe, WorkProbe, ListingWorkProbe uncovered; a read moved into a stored block lambda still counts → P3-29.
- P4-28: Bim.Gltf ProjectReference kept for the package pin → P4-29.
ARCHIVOS CAMBIADOS: tests/integration/fake-revit-command-pipe.ps1, tests/mirror-journal/MirrorDriftLoopChecks.cs; tests/cli/InternalErrorDelegationTests.cs; tests/mcp/GltfReferenceTests.cs, tests/mcp/Program.cs; tasks.md, docs/features.json, docs/contracts/module-contracts.md, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P10-31, P10-32, P6-30, P6-31, P5-30 (relay245); then P1-36, P1-37, P5-31, P7-24, P7-29 (relay246); then P2-35, P3-28, P3-29, P4-29. GenerateTypologyWrite.cs edits serialize (P3-28); InternalErrorDelegationTests.cs serializes (P3-29); P4-29 edits tests/cli/PackageLayoutPackTests.cs; McpServer.cs edits serialize (P10-31, P1-36 comment); tests/cli/SkillListTests.cs serializes (P5-30, P5-31); RevitCommandDeadlineTests.cs serializes (P1-36, P1-37); GenerateWrite.cs (P7-29) updates the P4-27 Run pin via a second documented delta.
```
