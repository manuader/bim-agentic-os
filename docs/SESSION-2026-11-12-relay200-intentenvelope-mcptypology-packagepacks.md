# Relay200: IntentPlan envelope, MCP generate_typology, pack lookup from a real package

Cloud scheduled relay, started 2026-11-12 15:52Z (LOCK was FREE after relay199). GitHub CI was the only oracle (no local .NET SDK or pwsh). Oldest-touched phases with ready work: 3, 5 and 7 (relay197). P7-16 waited (shares the generate-typology parser with P5-17; now depends on it). Workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (0 blockers, 5 should-fix, nits), findings closed with tests, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P3-15 (Phase 3): IntentPlan.ParseIntent/Build return IntentStep<T> (value or IntentEnvelope Line/Exit/Diagnostic); IntentPlan.Failed/Internal build the ApplyEditsCommand.Fail/Internal bytes; IntentEnvelope.Print is the single Console writer; DiffIntentCommand.Plan(args) writes nothing, Run = Plan(args).Print(); MCP DiffIntentTool calls Plan directly (no Console.Out capture, lock or exit-code save).
- P5-17 (Phase 5): MCP generate_typology {request_path} (permission read, readOnlyHint, no output path) calls GenerateTypologyCommand.Document; the verb's Execute was split into ReadRequest/Generate shared by both (CLI bytes unchanged); generate_typology_path_invalid guard.
- P7-17 (Phase 7): tests/cli/PackageLayoutPackTests.cs builds a real package with scripts/package.ps1 and checks package-first pack lookup for plan-layouts, generate-prototypes and generate-typology; no production change needed.
- Board: P3-15, P5-17, P7-17 ✅; new P3-16 (ApplyEditsCommand returns IntentEnvelope), P5-18 (packaged mcp/ server finds skill packs), P10-18 (macOS package SBOM attribution, ⛔ P10-17); P7-16 now after P5-17. Progress 174/193 (90.2 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P3-15 tests/cli + tests/mcp: red 43253372709, green 43255429352; review green 43273742972. 0 failed attempts.
- P5-17 tests/mcp: red 43253601980, green 43259222759; review green 43278984905. 1 failed attempt (tools/list order pins).
- P7-17 tests/cli: red 43253277222 (temporary negative check), green 43262717876; review green 43287543943. 0 failed attempts against the oracle.
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. Optional: repository licence.
BENCHMARKS / NÚMEROS REALES: package.ps1 build inside the cli test took seconds on Linux/Windows CI (two dotnet publish steps).
GAPS CONOCIDOS:
- P3-15: envelope JSON duplicated in ApplyEditsCommand.Fail/Internal (held equal by tests) (→ P3-16); IntentEnvelope.Print compiled into the MCP server unused.
- P5-17: packaged mcp/ server cannot find skill packs — SkillPackLookup recognises only a cli/ binary directory (→ P5-18); malformed-pack refusals not exercised through MCP; GltfExportCommand.cs linked into Bim.Mcp only to compile Execute.
- P7-17: macOS package.ps1 refuses first-party Bim.*.dll as sbom_unattributed (run 43257265603), so the test is a counted SKIP there via a tested single-problem predicate (→ P10-18); deviation from "SKIP only where pwsh is absent" accepted by the controller.
ARCHIVOS CAMBIADOS: interfaces/cli/{IntentPlan,DiffIntentCommand,ApplyIntentCommand,GenerateTypologyCommand}.cs; interfaces/mcp/{DiffIntentTool.cs,GenerateTypologyTool.cs (new),McpServer.cs,Bim.Mcp.csproj}; tests/cli/{IntentPlanTests.cs (new),PackageLayoutPackTests.cs (new),ApplyIntentTests.cs,Program.cs,Bim.Cli.Tests.csproj}; tests/mcp/{GenerateTypologyToolTests.cs (new),Program.cs,Bim.Mcp.Tests.csproj}; tests/revit-commands/{Bim.RevitCommands.Tests.csproj,Program.cs}; board/features/contracts/attempts/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P4-16/P4-17, P2-21, P10-17 (relay198; P4-16/P4-17 share owner-session files with P1-18); then P1-23 (then P1-24), P2-22, P6-18 (then P6-19) (relay199); then P3-16, P5-18/P7-16 (P5-18 and P7-17 share PackageLayoutPackTests; P7-16 edits GenerateTypologyCommand), P10-18 after P10-17. Owner: ADR0010/ADR0011; one -Catalogue run.
```
