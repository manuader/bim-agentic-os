# Relay239: generate-typology post-write cleanup, plan-layouts writer out of the MCP binary, MCP paths under a deleted cwd

Relay239 (cloud scheduled, 2026-11-19 01:52Z) took the FREE LOCK, picked the oldest-touched phases 3, 4 and 10 (relay233), got three workers green, ran three independent read-only reviews (0 blockers; should-fixes on P3-25 and P4-25 closed with tests) and integrated through one integration head re-run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh). The LOCK heartbeat was refreshed by the workers during CI waits.

```
IMPLEMENTADO:
- P3-25 (Phase 3): GenerateTypologyCommand.PostWriteProbe (null in production). In Execute everything after a successful GltfExporter.WriteNew sits in one try whose catch deletes the just-written output and rethrows to Run's P3-24 catch-all → generate_typology_internal_error, diagnostic once, exit 1. Defect fixed (the file used to stay on disk).
- P4-25 (Phase 4): PlanLayoutsCommand is a partial class; Run/Execute/Fail moved verbatim into CLI-only interfaces/cli/PlanLayoutsWrite.cs; Bim.Mcp.csproj (explicit links) does not compile it; tests/cli and tests/mcp csproj link it.
- P10-29 (Phase 10): the seven MCP tool PathProblem copies also catch FileNotFoundException → typed *_path_invalid for a relative path under a deleted working directory. Defect fixed (audit_verify gave an internal error, the other six McpServer's generic failure).
- Board: P3-25, P4-25, P10-29 ✅; new P3-26, P4-26, P10-30. Progress 252/271 (93.0 %). Contracts (plan-layouts files, P10-29, P3-25 sections) and features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P3-25: red 44440623128 (missing seam), green 44441914876, review mutation red 44449499959 (File.Delete removed → only the two deletion checks fail on core, core-windows, macOS), review green 44451605005. 0 failed attempts.
- P4-25: red 44444929450 (no plan-layouts writer split), green 44446308443, review green 44453950535. 0 failed attempts (2 red-phase test-only fixes: CS0136, a self-test resolving against real disk).
- P10-29: probe red 44440575516 (all 15 cases on Linux), green 44442993811. 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: P3-25 core 4144 passed / 2 failed in the mutation run; P10-29 15 path parameters across 7 tools.
GAPS CONOCIDOS:
- P3-25: a failing cleanup delete is silent (→ P3-26); theoretical replace race between WriteNew and Delete; a partial success line may precede the typed line if stdout throws mid-write (documented). Scope: comment edits outside Execute.
- P4-25: Bim.Mcp still compiles the generate-typology (→ P4-26) and generate-prototypes writers. Scope: tests/mcp csproj link hunk was required (P7-22 in-process Run call).
- P10-29: tests/mcp does not run on macOS in CI (Linux evidence + Windows SKIP); CLI verbs not probed (→ P10-30); seven identical PathProblem copies remain.
ARCHIVOS CAMBIADOS: interfaces/cli/GenerateTypologyCommand.cs, tests/cli/GenerateTypologyTests.cs; interfaces/cli/PlanLayoutsCommand.cs, interfaces/cli/PlanLayoutsWrite.cs (new), tests/cli/Bim.Cli.Tests.csproj, tests/mcp/Bim.Mcp.Tests.csproj, tests/mcp/PlanLayoutsWriteLinkTests.cs (new), tests/mcp/PlanLayoutsToolTests.cs, tests/mcp/GltfExportLinkTests.cs (comment); interfaces/mcp/{AuditVerify,DiffIntent,DocRealized,DocUpdate,GeneratePrototypes,GenerateTypology,PlanLayouts}Tool.cs, tests/mcp/DeletedCwdPathToolTests.cs (new), tests/mcp/Program.cs; tasks.md, docs/features.json, docs/contracts/module-contracts.md, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P5-22, P5-27, P6-27, P6-28, P1-33 (relay236); then P7-24, P7-26, P1-34, P2-32 (relay238); then P3-22, P3-26, P4-26, P10-27, P10-30. GenerateTypologyCommand.cs edits serialize (P3-26, P4-26, P10-30); McpServer.cs edits serialize (P5-22, P10-27); csproj edits serialize (P4-26).
```
