# Relay253: macOS client send buffer, generate-typology output write failures, package mcp/ without Bim.Gltf

Relay253 (cloud scheduled, 2026-11-20 19:52Z) took the FREE LOCK, picked the oldest-touched phases 2, 3 and 4 (relay250), got three workers green, ran three independent read-only reviews (all approve, 0 blockers; nits recorded as follow-up tasks) and integrated through one integration head run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh).

```
IMPLEMENTADO:
- P2-36 (Phase 2): [ThreadStatic] RevitCommandService.PipeConnected test seam (null in production, read once at the top of WriteRequest). On macOS the mirror-journal loop raises the client socket's SO_SNDBUF 2× through it: queued == raised, handed == 4096 + raised; the lever-free CLI process stays at the default send buffer. Seam hygiene pinned.
- P3-30 (Phase 3): generate-typology Run catch (Refusal) sets exit 1 before a guarded Fail; catch-all sets exit before a guarded typed print; InternalRefusal guards its stderr write. DEFECTS FIXED: a throwing stdout escaped Run with exit 0 (refusal and catch-all paths); a throwing stderr in InternalRefusal produced a second internal error (and escaped MCP Document).
- P4-30 (Phase 4): test-only. The package built by scripts/package.ps1 has no mcp/Bim.Gltf* file, sbom or manifest line; cli/Bim.Gltf.dll positive control; planted self-tests; package fingerprint unchanged.
- Board: P2-36, P3-30, P4-30 ✅; new P2-37, P3-31, P4-31. Three blank lines splitting phase tables and three rows with an extra empty cell (relay252 integration) fixed, so Progress counts every row. Progress 294/313 (93.9 %). Contracts and features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P2-36: red 44806774783 (CS0117 PipeConnected), green 44807834467. 0 failed attempts.
- P3-30: red 44807001866 (stdout failures escaped Run with exit 0), green 44810121962. 0 failed attempts.
- P4-30: deliberate probe red 44806654711 (planted mcp/Bim.Gltf.dll caught), green 44809450020. 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: none (macOS raised-buffer byte counts not captured; → P2-37).
GAPS CONOCIDOS:
- P2-36: exact macOS counts not recorded; precondition lacks raised % 1024 == 0; one raised value only; the lever reads internal SafePipeHandle.PipeSocket by reflection (fails closed) → P2-37.
- P3-30: Document's own catch-all (UnguardedWorkProbe path) still writes stderr unguarded; partial stdout write not measured; disposed-stdout refusal attempt not counted → P3-31.
- P4-30: non-canonical sbom paths, named failure on a missing control file, tests/packaging/test-package.ps1 parity, mcp deps.json pin → P4-31.
ARCHIVOS CAMBIADOS: core/Bim.Core/RevitCommandsService.cs, tests/mirror-journal/MirrorDriftLoopChecks.cs; interfaces/cli/GenerateTypologyWrite.cs, interfaces/cli/GenerateTypologyCommand.cs, tests/cli/GenerateTypologyTests.cs, tests/mcp/GenerateTypologyWriteLinkTests.cs, tests/cli/InternalErrorDelegationTests.cs; tests/cli/PackageLayoutPackTests.cs; tasks.md, docs/features.json, docs/contracts/module-contracts.md, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P10-32, P10-34, P6-32, P6-33, P7-32 (relay251); then P1-38, P1-39, P5-32, P5-33, P7-33 (relay252); then P2-37, P3-29, P3-31, P4-31.
```
