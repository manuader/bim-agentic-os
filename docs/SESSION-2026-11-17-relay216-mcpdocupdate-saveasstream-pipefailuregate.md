# Relay216: doc_update MCP tool, streamed save_as hash, pipe-fake failure gate

Relay216 (cloud scheduled, 2026-11-17 04:53Z) took the FREE LOCK, picked the oldest-touched phases 1, 2 and 4 (relay214), got three workers green, ran three independent read-only reviews, closed the P4-20 should-fix with tests on its branch, and integrated through one integration head re-run in CI before main moved. P7-20 was left for a later relay because it registers in the same McpServer.cs/tool-list pins as P4-20. CI is the only oracle (no local .NET SDK or pwsh).

```
IMPLEMENTADO:
- P4-20 (Phase 4): read-only MCP tool doc_update {before, after, realized, bindings, dependents?} sharing DocUpdateCommand.DryPlan with the CLI verb (text byte-equal to the CLI output file; refusals equal the CLI line; nothing written). Lone surrogates inside input-file text are now typed doc_update_*_invalid in CLI and tool (was an unhandled crash).
- P1-26 (Phase 1): catalogue-report save_as hashes the -guardado.rvt in a stream (shared HashStream, copy cap 8 GiB); a >1 GiB file reports matches/mismatch; copy bytes unchanged.
- P2-26 (Phase 2): the pipe fake's -RecorderFailures injects only after a succeeded, non-dry-run reply; never-matched ids go to stderr and the fake exits 1.
- Board: P1-26, P2-26, P4-20 ✅. New: P1-27 (big-file test hygiene + cross-platform over-cap case), P2-27 (exit == 1, dry_run:false injects), P4-22 (MCP binary without probe/execute code). Progress 210/228 (92.1 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P4-20: red 44020772433, green 44022220986; review fix red 44025770910, green 44026934842. 1 failed attempt (static guard banned "wall_types" in every MCP file; narrowed to doc-update entry points, no behaviour check loosened).
- P1-26: red 44020476970, green 44021609472. 0 failed attempts.
- P2-26: red 44020700125, green 44022729310. 0 failed attempts.
- Reviews (independent, read-only): 0 blockers. P4-20 should-fix (lone surrogate crash) closed with tests; P4-20 out-of-set csproj/pin edits accepted (needed by the build); P1-26 and P2-26 should-fixes (stale contracts/features text) closed at integration; nits → P1-27, P2-27, P4-22.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P4-20: MCP binary links DocUpdateProbe/DocUpdateExecute (unreachable; → P4-22); relative paths resolve against the server working directory; write check snapshots only inputs/.
- P1-26: the real >1 GiB case runs on Linux only; big-file delete not in finally (→ P1-27).
- P2-26: exit asserted != 0, dry_run:false untested (→ P2-27); unmatched-id line only after a normal serve-loop end.
ARCHIVOS CAMBIADOS: interfaces/mcp/{DocUpdateTool.cs,McpServer.cs,Bim.Mcp.csproj}; interfaces/cli/{DocUpdateCommand,CatalogueReportCommand}.cs; tests/mcp/{DocUpdateToolTests,Program}.cs, tests/mcp/Bim.Mcp.Tests.csproj; tests/cli/{CatalogueReportTests,InternalErrorDelegationTests}.cs, tests/cli/Bim.Cli.Tests.csproj; tests/revit-commands/{Program.cs,Bim.RevitCommands.Tests.csproj}; tests/integration/fake-revit-command-pipe.ps1; tests/mirror-journal/MirrorDriftLoopChecks.cs; docs/contracts/module-contracts.md; board/features/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P7-20 (McpServer.cs, serialize with P4-21/P4-22/P5-22), P5-21 (relay215/214); P6-22/P6-23, P3-20, P10-23/P10-24 (relay215/213); then P4-21, P4-22, P1-27, P2-27. Owner: ADR0010/ADR0011; one -Catalogue run.
```
