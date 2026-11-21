# Relay256: macOS send buffer values, typology Document stderr, package Bim.Gltf normalised

Relay256 (cloud scheduled, 2026-11-21 01:52Z) took the FREE LOCK, picked the oldest-touched phases 2, 3 and 4 (relay253), got three workers green, ran one independent read-only review of all three branches (all approve, 0 blockers; nits recorded as follow-up tasks) and integrated through one integration head run in CI before main moved. CI is the only oracle (no local .NET SDK).

```
IMPLEMENTADO:
- P2-37 (Phase 2): test-only (+ comment in RevitCommandsService.cs, deviation). macOS precondition requires raised and lowered % 1024 == 0; second value = lowered ½ send buffer through the same lever; measured macOS counts recorded (default 8192; raised 16384 → queued 16384 / handed 20480; lowered 4096 → queued 4096 / handed 8192; CLI queued 8192).
- P3-31 (Phase 3): DEFECT FIXED — GenerateTypologyCommand.Document catch-all stderr write escaped Document on a throwing stderr; now guarded, (false, typed line), one attempt. Partial stdout pinned one way (as P7-32).
- P4-31 (Phase 4): test-only. McpWithoutGltf path normalisation, named failures (no exceptions), first differing fingerprint line, mcp deps.json pin; test-package.ps1 parity section.
- Board: P2-37, P3-31, P4-31 ✅; new P2-38, P3-32, P4-32. Progress 303/322 (94.1 %). Contracts and features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P2-37: probe red 44853016956, green 44854474484. 0 failed attempts.
- P3-31: red 44853216081, green 44854965548. 0 failed attempts.
- P4-31: red 44853585847, green 44855334575. 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: macOS CI runner send buffer counts above (pipe drift loop, wire 39757 bytes, read 4096, fake InBufferSize 32768).
GAPS CONOCIDOS:
- P2-37: lowered value equals the 4096 read size (only the raised run separates read + value from 2 × read); precondition guard repeated → P2-38.
- P3-31: MCP tool itself not exercised with a throwing stderr; writer helpers lack WriteLine()/Write(char[],int,int); regex pin brittle; contract note → P3-32.
- P4-31: `..` above root clamped silently; extensionless deps runtime asset missed; P10-18 block parses JSON unguarded; ps1 copy without -Force; header comment → P4-32.
ARCHIVOS CAMBIADOS: tests/mirror-journal/MirrorDriftLoopChecks.cs, core/Bim.Core/RevitCommandsService.cs (comment); interfaces/cli/GenerateTypologyCommand.cs, tests/cli/GenerateTypologyTests.cs; tests/cli/PackageLayoutPackTests.cs, tests/packaging/test-package.ps1; tasks.md, docs/features.json, docs/contracts/module-contracts.md, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P10-34, P10-35, P6-33, P6-34, P7-34 (relay254); then P1-39, P1-40, P5-33, P5-34, P7-35 (relay255); then P2-38, P3-29, P3-32, P4-32.
```
