# Relay225: doc_update MCP dry-plan split, bcf-export newline timestamps, DXF block-region CLI

Relay225 (cloud scheduled, 2026-11-17 23:52Z) took the FREE LOCK, picked the oldest-touched phases 4, 10 (relay220) and 6 (relay221), got three workers green, ran three independent read-only reviews (0 blockers; should-fixes on P10-25 and P6-23 closed with tests on the task branches) and integrated through one integration head re-run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh). The lock heartbeat was refreshed every 20 minutes.

```
IMPLEMENTADO:
- P4-22 (Phase 4): DocUpdateCommand is a partial class — verb half (Usage, Run, Execute, TryParse) in DocUpdateCommand.cs, dry-plan half moved verbatim to interfaces/cli/DocUpdateDryPlan.cs. Bim.Mcp.csproj and tests/mcp/Bim.Mcp.Tests.csproj link only DocUpdateDryPlan.cs; tests/mcp/DocUpdateLinkTests.cs parses csproj XML (Include/Link, ';' lists, wildcards, separators, case) and comment-stripped linked sources for DocUpdateProbe./DocUpdateExecute., with checker self-tests. CLI and tool bytes unchanged.
- P10-25 (Phase 10): bcf-export trailing-newline timestamps (Z and +02:00) and +200 refused at $.project.timestamp; in-process NUL test captures Console.Error (empty) and restores Out/Error/ExitCode in a finally; comment corrected (zzz alone takes +hhmm, +h:mm; refuses +hh and +hmm) with runtime fixtures.
- P6-23 (Phase 6): tests/cli/CadBriefBlockRegionTests.cs — generated DXF with the Dormitorio region only in block ROOM (INSERT I1) through the real CLI cad-brief with and without --include-block-regions equals in-process BriefExtract rooms (InsertPath [I1] only with the flag); --page/--metres-per-unit followed by the flag refused cad_option_value_invalid, empty stderr, nothing written.
- Board: P4-22, P10-25, P6-23 ✅; new P6-26 (nested/MINSERT/mirrored inserts). Progress 228/246 (92.7 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P4-22: red 44203790617, green 44204921152. 0 failed attempts.
- P10-25: green 44203380992, 44207466850 (no red: behaviour already held), review fix 038c6b8. 0 failed attempts.
- P6-23: probe red 44204728280, green 44206837154, review fix 0304e16. 1 failed attempt (CS0136 compile error).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P4-22: a wildcard Include behind an MSBuild property or a transitive/Import-ed reference is not checked (low risk, none exist); Bim.Mcp.csproj still links GltfExportCommand.cs (→ P4-23) and RevitDeadlineTransport.cs.
- P10-25: the zzz fixtures document the .NET runtime, not the product.
- P6-23: single-level INSERT only (→ P6-26).
ARCHIVOS CAMBIADOS: interfaces/cli/{DocUpdateCommand,DocUpdateDryPlan(new),BcfExportCommand}.cs; interfaces/mcp/{Bim.Mcp.csproj,DocUpdateTool.cs(comment)}; tests/mcp/{DocUpdateLinkTests(new),DocUpdateToolTests,Program,Bim.Mcp.Tests.csproj}; tests/cli/{CadBriefBlockRegionTests(new),BcfExportCommandTests,Program,InternalErrorDelegationTests,Bim.Cli.Tests.csproj}.cs; tests/revit-commands/Bim.RevitCommands.Tests.csproj; tests/integration/fake-doc-update.ps1 (comment); docs/contracts/module-contracts.md; docs/agent/ATTEMPTS.md; board/features/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P5-22/P7-21 (McpServer.cs + tool-list pins — serialize), P5-24, P6-25 (relay221); then P1-29..P1-31, P2-29, P3-22, P3-23 (relay223); then P4-23, P10-26, P6-26. Owner: ADR0010/ADR0011; one -Catalogue run.
```
