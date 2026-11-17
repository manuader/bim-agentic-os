# Relay220: MCP doc_realized, sparse save_as on all OSes, typed lock path in jurisdiction-resolve

Relay220 (cloud scheduled, 2026-11-17 12:53Z) took the FREE LOCK, picked the oldest-touched phases 4, 1 (relay216) and 10 (relay215/219 left P10-24), got three workers green, ran three independent read-only reviews (0 blockers, 0 should-fix) and integrated through one integration head re-run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh).

```
IMPLEMENTADO:
- P4-21 (Phase 4): read-only MCP tool doc_realized {record, fixture, assignments, document} (permission read, readOnlyHint). DocRealizedCommand split into a private Build shared by Run and the new internal DryRealized; envelope text in Line(). Tool returns DocRealized.Canonical bytes equal to the CLI output file; input_*/realized_* refusals equal the CLI line; forced internal error → doc_realized_internal_error (IntentPlan) with one stderr diagnostic; MCP-only doc_realized_path_invalid before any read. tools/list pins updated in tests/mcp and tests/revit-commands; csproj links in Bim.Mcp and 3 test projects.
- P1-27 (Phase 1): catalogue-report big save_as test helper WithSparseSaveAs deletes the 1 GiB + 1 file in a finally (forced-throw case pinned); Linux-only gate removed — Windows marks the file sparse (FSCTL_SET_SPARSE), macOS/Linux use SetLength holes; on-disk size < 64 MiB checked. Pins unchanged.
- P10-24 (Phase 10): jurisdiction-resolve checks --lock with SkillLockTool.PathProblem right after usage — NUL / >4096-char path → {"status":"failed","code":"skills_lock_path_invalid","detail":"lock"} (same as skills-lock); absent lock pins lock_missing; registry issues dropped from the success envelope (contract states it; pinned with a claim_duplicate + alias_overlap package).
- Board: P4-21, P1-27, P10-24 ✅. New: P1-28, P4-23, P10-26 (review nits). Progress 219/238 (92.0 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P4-21: red 44088162610, green 44092911934. 1 failed attempt (44090048314: the test reused a JsonObject already attached to a request; fixed by a fresh arguments object per call).
- P1-27: red 44087848778 (Linux, Windows and macOS each failed exactly the forced-throw check), green 44090697504. 0 failed attempts.
- P10-24: red 44087836799 (exactly the 2 new in-process checks), green 44089951746. 0 failed attempts.
- Reviews (independent, read-only, static — no SDK on the host): 0 blockers, 0 should-fix; nits recorded as P1-28, P4-23, P10-26.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: none new (P1-27 adds a few seconds per OS hashing sparse 1 GiB files).
GAPS CONOCIDOS:
- P4-21: in-process "no stdout" check only proves Console.Out was not swapped; MCP binary still compiles the doc-realized write path (unreachable, statically banned) (→ P4-23).
- P1-27: missing du / no sparse support aborts the tests/cli run instead of skipping; forced-throw case hashes 1 GiB before throwing (→ P1-28).
- P10-24: ExitCode restore and twin-pack delete not in finally; GetFullPath-only rejection unpinned (→ P10-26).
ARCHIVOS CAMBIADOS: interfaces/mcp/{DocRealizedTool.cs (new),McpServer.cs,Bim.Mcp.csproj}; interfaces/cli/{DocRealizedCommand,JurisdictionResolveCommand}.cs; tests/mcp/{DocRealizedToolTests.cs (new),Program.cs,Bim.Mcp.Tests.csproj}; tests/revit-commands/{Program.cs,Bim.RevitCommands.Tests.csproj}; tests/cli/{CatalogueReportTests,JurisdictionResolveTests}.cs, tests/cli/Bim.Cli.Tests.csproj; docs/contracts/module-contracts.md; board/features/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P2-28, P3-21, P3-22 (relay219); P5-22/P7-21 (McpServer.cs + tool-list pins + 4 csprojs — serialize), P5-23, P6-23/P6-24, P7-22 (relay217); then P4-22/P4-23 (serialize on Bim.Mcp.csproj), P1-28, P10-25/P10-26. Owner: ADR0010/ADR0011; one -Catalogue run.
```
