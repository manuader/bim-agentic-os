# Relay187: doc-update --execute, pipe read timeout, release SBOM

Cloud scheduled relay, started 2026-11-11 04:53Z (LOCK was FREE after relay186). GitHub CI was the only oracle (no local .NET SDK; pwsh 7.4.6 downloaded for board/progress and packaging fixture checks). Oldest-touched phases with ready work: 4 and 10 (relay184), then 1 (relay185). Workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (0 blockers, 12 should-fix), every finding closed with tests, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P4-12 (Phase 4): `doc-update ... --probe-dependents pipe --execute --expected-document plan-sha256` runs the P4-10 plan once through DocUpdate.Run after a plan-hash guard; per-step report done/failed/not_sent with reply_lost (no_response, pipe_io_error, pipe_read_timeout, response_invalid), `sent`, replayed only for earlier-invocation receipts; report kept on library exceptions; dry plan bytes unchanged.
- P1-17 (Phase 1): client-side `pipe_read_timeout` after a 1800 s default (API-configurable, 24 h cap); writes report outcome unknown with replay guidance, never retried; PlanRunner/paging/DocUpdateDependents map it; owner -Catalogue stops on it; wire bytes pinned; contracts updated.
- P10-13 (Phase 10): canonical sbom.json in the release package (assemblies with version/licence/sha256, direct NuGet references with normalised versions and nuspec SPDX licence), listed in manifest.json; package refuses unknown licences/unpinned refs; package-verify refuses missing/invalid/build-only/mismatched sboms.
- Board: P4-12, P1-17, P10-13 ✅; new P1-20 (CLI/MCP read-timeout knob), P4-14 (owner step uses doc-update --execute), P10-14 (transitive NuGet attribution). Progress 141/160 (88.1 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P4-12 tests/cli: red 42962945423, green 42963892631; review red 42968757425, green 42969538210. 0 failed attempts.
- P1-17 tests/revit-commands, tests/plan-to-commands, tests/doc-regeneration, tests/integration: red 42962679193, green 42964972283; review red 42971664563, green 42972760593. 3 failed attempts (ATTEMPTS).
- P10-13 tests/packaging: red 42962728184, green 42966315083; review red 42974651912, green 42974975958. 1 failed attempt (ATTEMPTS).
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; P4-11 / P1-14 owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. New, optional: declare a licence for the repository (first-party Bim.* assemblies show NOASSERTION in the SBOM).
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P4-12: no resume; report on stdout only; exception-path report covers plan-file steps only.
- P1-17: only the reply read is bounded; CLI/MCP have no knob yet (P1-20); Windows close-without-reply is now no_response (was pipe_io_error).
- P10-13: real package has no NuGet references today (reference path synthetic only); transitive packages not attributed (P10-14); not CycloneDX/SPDX; unsigned.
ARCHIVOS CAMBIADOS: interfaces/cli/{DocUpdateExecute,DocUpdateCommand,DocUpdateProbe,Program}.cs; core/Bim.Core/{RevitCommands,RevitCommandsService,PlanRunner,RevitCommandsQueryPaging,DocUpdateDependents}.cs; scripts/{package,package-common,package-verify,owner-catalogue-steps}.ps1; tests/cli/{DocUpdateExecuteTests,Program}.cs, tests/cli/Bim.Cli.Tests.csproj; tests/revit-commands/{PipeReadTimeout,Program,ElementQueryPaging}.cs; tests/plan-to-commands/PlanRunnerTests.cs; tests/doc-regeneration/UpdateDependents.cs; tests/integration/test-catalogue-session.ps1; tests/packaging/{test-package,test-install-addin,test-package-archive}.ps1; board/features/contracts/attempts/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P1-16/P1-18/P1-20, P2-14/P2-15, P6-14 (relay185; P1-16 and P1-20 both touch interfaces/mcp — serialize); then P7-12, P3-12, P5-14, P7-14 (relay186; P7-12/P7-14 share SkillPackLookup — serialize); then P4-13/P4-14, P10-14 (relay187; P4-14 and P1-18 share tests/integration/test-catalogue-session.ps1 — serialize). Owner: ADR0010/ADR0011; one -Catalogue run.
```
