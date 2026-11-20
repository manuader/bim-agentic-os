# Relay252: late-cancel real pipe, skill-list diagnostic write, packaged MCP prototypes handshake

Relay252 (cloud scheduled, 2026-11-20 17:53Z) took the FREE LOCK, picked the oldest-touched phases 1, 5 and 7 (relay249), got three workers green, ran three independent read-only reviews (all approve, 0 blockers; nits recorded as follow-up tasks) and integrated through one integration head run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh).

```
IMPLEMENTADO:
- P1-37 (Phase 1): test-only. Static pin (comment- and literal-stripped, 13 mutants) that ReadReplyLine attaches exactly one OnlyOnFaulted continuation observing the abandoned read; real stalled in-process NamedPipe probe: no UnobservedTaskException after each abandoned read is collected; end states pinned per OS.
- P5-31 (Phase 5): skill-list LoadedProbe seam after JurisdictionRegistry.Load (null in production); guarded SkillListCommand.WriteDiagnostic used by Run and MCP SkillListTool. DEFECT FIXED: a throwing Console.Error escaped Run (and became a generic Failed message through MCP).
- P7-31 (Phase 7): test-only. Packaged MCP initialize/tools/list/tools/call handshake in one process, ranked document sha256 pinned, pack_invalid parity CLI/MCP, repository fallback with VERSION or manifest.json removed, pack_unavailable and repository_not_found refusals.
- Board: P1-37, P5-31, P7-31 ✅; new P1-39, P5-33, P7-33. Progress 291/310 (93.9 %). Contracts and features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P1-37: deliberate probe red 44788387750 (measured: production read canceled; pipe disposed under an ignoring read faulted on Linux/macOS, completed on Windows; decoder fault faulted), green 44791397869, final 44791452047. 0 failed attempts.
- P5-31: red 44788501109 (CS0117 LoadedProbe), green 44790962935. 0 failed attempts.
- P7-31: deliberate probe red 44788128345 (sha pin zeros; observed 436d4072…709f22d, identical Linux/Windows), green 44791328722. 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: none.
GAPS CONOCIDOS:
- P1-37: step E (production transport) has a GC loop but no collection proof (label overstates); StreamReader-frame filter branch has no control; Windows B "completed" result not recorded; two strip helpers to merge → P1-39.
- P5-31: a throwing Console.Out still escapes skill-list Run; jurisdiction-resolve's diagnostic write is unguarded; no real-process closed-stderr run; one line in interfaces/mcp/SkillListTool.cs outside the owned set (documented delta ToolDeltaP531) → P5-33.
- P7-31: no infeasible-document sha, generate_typology/plan_layouts packaged fallback, full tools/list catalogue pin or macOS packaging → P7-33.
ARCHIVOS CAMBIADOS: tests/cli/RevitCommandDeadlineTests.cs; interfaces/cli/SkillListCommand.cs, interfaces/mcp/SkillListTool.cs, tests/cli/SkillListTests.cs, tests/mcp/SkillListToolTests.cs; tests/packaging/test-package.ps1; tasks.md, docs/features.json, docs/contracts/module-contracts.md, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); pwsh -File tests/packaging/test-package.ps1.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P2-36, P3-29, P3-30, P4-30 (relay250); then P10-32, P10-34, P6-32, P6-33, P7-32 (relay251); then P1-38, P5-32, P5-33, P7-33.
```
