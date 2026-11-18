# Relay226: skill-list single read, MCP generate_prototypes, catalogue-report exit gate

Relay226 (cloud scheduled, 2026-11-18 01:52Z) took the FREE LOCK, picked the oldest-touched phases 5, 7 (relay221) and 1 (relay223), got three workers green, ran three independent read-only reviews (0 blockers; should-fixes on P5-24 and P1-29 closed with tests on the task branches) and integrated through one integration head re-run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh). The lock heartbeat was refreshed on each task edge.

```
IMPLEMENTADO:
- P5-24 (Phase 5): JurisdictionLoad.LoaderError (SkillException?, init) set by JurisdictionRegistry.Load and LoadLocked on their skills_invalid refusal; skill-list prints it as {status,code,field,detail,file} and never calls SkillLoader or the filesystem itself; race branch removed. Static check uses a C# comment/literal lexer and bans LoadDirectory(, LoadLocked(, SkillPackHash., SkillLoader (incl. using static / alias), File./Directory. and stream/info types, with 21 checker self-tests.
- P7-21 (Phase 7): read-only MCP tool generate_prototypes {request} (permission read). GenerateCommand.Execute split into ReadRequest/Generate/Document (+FailureLine); CLI and tool share Generate(ReadRequest(path)). Bad path → generate_prototypes_path_invalid; internal failure → typed generate_prototypes_internal_error. Linked in Bim.Mcp.csproj and the three test projects; tools/list pins extended.
- P1-29 (Phase 1): catalogue-report --fail-on-failures (before or after --run, once) exits 2 when overall.failed > 0, copy state ≠ match or a printed save_as state ≠ matches; stdout bytes identical; repeated/unknown flag → catalogue_report_usage; global usage lists the flag; contract updated.
- Board: P5-24, P7-21, P1-29 ✅; new P5-25 (LoaderError invariant), P7-23 (generate_prototypes refusal coverage), P7-24 (packaged MCP). Progress 231/249 (92.8 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P5-24: red 44219312842, green 44219413459; review red 44225252963, green 44225380175. 0 failed attempts.
- P7-21: red 44219627281, green 44219938454. 0 failed attempts.
- P1-29: red 44219479345, green 44221871058; review green 44230381758. 1 failed attempt (usage pin compared JSON-escaped < >).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. Optional: should catalogue-report --fail-on-failures treat v1-only runs (copy not_in_run) as neutral? Today they exit 2 (fail closed).
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P5-24: LoaderError is a public init on a positional record (`with` can keep a stale error) → P5-25.
- P7-21: no infeasible/repository_not_found/pack_invalid through the tool, no stdio refusal, parse/load defects outside the guarded block are generic → P7-23; no packaged MCP test → P7-24.
- P1-29: the realized (doc-up-elements) member does not gate the exit code.
ARCHIVOS CAMBIADOS: core/Bim.Core/{JurisdictionRegistry,JurisdictionLock}.cs; interfaces/cli/{SkillListCommand,GenerateCommand,CatalogueReportCommand,Program}.cs; interfaces/mcp/{GeneratePrototypesTool(new),McpServer}.cs, Bim.Mcp.csproj; tests/cli/{SkillListTests,CatalogueReportTests}.cs, Bim.Cli.Tests.csproj; tests/Bim.Tests/Program.cs; tests/mcp/{GeneratePrototypesToolTests(new),Program}.cs, Bim.Mcp.Tests.csproj; tests/revit-commands/{Program.cs,csproj}; docs/contracts/module-contracts.md; docs/agent/ATTEMPTS.md; board/features/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P2-29, P3-22, P3-23 (relay223); then P4-23, P10-26, P6-25/P6-26 (relay225); then P5-22 (McpServer.cs — serialize with P7-23/P7-24 if they touch it), P5-25, P7-23, P7-24, P1-30, P1-31. Owner: ADR0010/ADR0011; one -Catalogue run.
```
