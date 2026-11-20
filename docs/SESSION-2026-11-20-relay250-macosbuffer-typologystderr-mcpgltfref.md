# Relay250: macOS queued-bytes cause, generate-typology stderr containment, MCP without Bim.Gltf

Relay250 (cloud scheduled, 2026-11-20 13:53Z) took the FREE LOCK, picked the oldest-touched phases 2, 3 and 4 (relay247), got three workers green, ran three independent read-only reviews (0 blockers; the P2-35 should-fix and nits closed with tests, the rest recorded as follow-up tasks) and integrated through one integration head run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh).

```
IMPLEMENTADO:
- P2-35 (Phase 2): test-only. On macOS the fake raises its pipe socket receive buffer to 4× its measured default (cap 1 MiB) before the -BreakAfterBytes connection's first read and stabilises FIONREAD (50 ms polls, ≤ ~1 s). Measured: InBufferSize 32768, queued 8192 == client default send buffer, handed 12288 == 4096 + send buffer → the writer's send buffer is the limit on XNU AF_UNIX. Pin rewritten as relations behind a separate precondition check; Linux/Windows unchanged.
- P3-28 (Phase 3): generate-typology Run catch-all writes the internal-error diagnostic in its own try/catch and prints the envelope without it (P7-28 shape). A throwing stderr on every forced path still prints generate_typology_internal_error once with exit 1; P3-25/P3-26 cleanup unchanged; success, refusal and section 10/11 bytes unchanged. P4-26 Run pin via documented delta RunDeltaP328; ForbiddenUse covers PostWriteRemovalProbe/PartialOutputLeft.
- P4-29 (Phase 4): Bim.Mcp.csproj no longer references Bim.Gltf (no transitive route); the package no longer requires mcp/Bim.Gltf.dll (cli/Bim.Gltf.dll still required); GltfReferenceTests requires zero references and a deps.json without Bim.Gltf. tools/list and every MCP pin unchanged.
- Board: P2-35, P3-28, P4-29 ✅; new P2-36, P3-30, P4-30. Progress 285/304 (93.8 %). Contracts, features.json and the tests/mcp/Program.cs registration comment updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P2-35: deliberate macOS probe red 44748588269, green 44752497640; review fixes green 44757352462. 0 failed attempts.
- P3-28: red 44748851174 (ForbiddenUse self-test), red 44750557720 (7 stderr-throw cases escaped Run, exit 0), green 44752812933. 0 failed attempts.
- P4-29: red 44748491831 (csproj still references Bim.Gltf), green 44751511169. 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: macOS AF_UNIX: default send buffer 8192 bounds queued bytes at a 4096-byte break (see P2-35).
GAPS CONOCIDOS:
- P2-35: UnixSendBuffer measures a fresh socket, not the CLI's own pipe socket; no client-side lever proves the cause; FIONREAD poll count not logged → P2-36.
- P3-28: a throwing Console.Out in Run's catch-all still escapes with exit 0; InternalRefusal's stderr write is unguarded (contained, but the diagnostic is attempted twice) → P3-30.
- P4-29: no explicit package check that mcp/ holds no Bim.Gltf.dll (deps.json covers it indirectly) → P4-30.
ARCHIVOS CAMBIADOS: tests/integration/fake-revit-command-pipe.ps1, tests/mirror-journal/MirrorDriftLoopChecks.cs; interfaces/cli/GenerateTypologyWrite.cs, tests/cli/GenerateTypologyTests.cs, tests/mcp/GenerateTypologyWriteLinkTests.cs; interfaces/mcp/Bim.Mcp.csproj, tests/cli/PackageLayoutPackTests.cs, tests/mcp/GltfReferenceTests.cs, tests/mcp/Program.cs (comment); tasks.md, docs/features.json, docs/contracts/module-contracts.md, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P10-32, P10-33, P6-31, P6-32, P7-30 (relay248); then P1-37, P1-38, P5-31, P5-32, P7-31 (relay249); then P2-36, P3-29, P3-30, P4-30.
```
