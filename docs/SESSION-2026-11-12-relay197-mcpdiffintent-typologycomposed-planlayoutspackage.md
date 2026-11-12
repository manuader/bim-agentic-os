# Relay197: MCP diff_intent, generate-typology composed rules, plan-layouts --pack from the package

Cloud scheduled relay, started 2026-11-12 06:52Z (LOCK was FREE after relay195). GitHub CI was the only oracle (no local .NET SDK or pwsh). Oldest-touched phases with ready work: 3, 5 and 7 (relay193). P7-16 waited (same GenerateTypologyCommand as P5-16). Workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (0 blockers, 7 should-fix, nits), every finding closed with tests or recorded docs, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P3-14 (Phase 3): MCP tool diff_intent {project_path, intent_path} (read, readOnlyHint) runs the linked CLI DiffIntentCommand/IntentPlan — plans and refusals byte-equal to the CLI; typed diff_intent_path_invalid; writes nothing; static checks for the shared path and no write verbs in interfaces/mcp.
- P5-16 (Phase 5): generate-typology request members skills/precedence (≤ 64 skill ids each) run TypologyPipeline.RunComposed with package-first extra packs; resolver refusals typed; absent members keep the P7-14 bytes.
- P7-15 (Phase 7): plan-layouts --pack reads the release package first (SkillPackLookup packageFirst), byte-equal to the repository run; P7-12 refusal pin flipped to success; partial-marker stray skills/ ignored.
- Board: P3-14, P5-16, P7-15 ✅; new P3-15 (IntentPlan returns its envelope, drop Console capture), P5-17 (MCP generate_typology sharing the CLI parser), P7-17 (pack lookup against a real scripts/package.ps1 package). Progress 165/184 (89.7 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P3-14 tests/mcp: red 43171708125, green 43173176518; review red 43176571475, green 43178665401. 0 failed attempts.
- P5-16 tests/cli: red 43171602064, green 43173255032; review red 43181804179, green 43184471713. 0 failed attempts.
- P7-15 tests/cli: red 43171474254, green 43172800830; review green 43187267711. 0 failed attempts.
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; P4-11 / P1-14 owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. Optional: repository licence.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P3-14: process-wide Console.Out/ExitCode capture (one request at a time; → P3-15); paths not inline JSON; ProjectStore still allowed in existing MCP tools by design.
- P5-16: shipped style packs add no planner rule on tested lots (only a stricter package pack changes bytes); not on MCP (→ P5-17).
- P7-15: no real scripts/package.ps1 package in tests (→ P7-17); legacy Load(id) overload kept for tests.
ARCHIVOS CAMBIADOS: interfaces/mcp/{DiffIntentTool.cs (new),McpServer.cs,Bim.Mcp.csproj}; tests/mcp/{Program.cs,Bim.Mcp.Tests.csproj}; tests/cli/Bim.Cli.Tests.csproj; tests/revit-commands/{Program.cs,Bim.RevitCommands.Tests.csproj}; interfaces/cli/{GenerateTypologyCommand,PlanLayoutsCommand,SkillPackLookup}.cs; tests/cli/{GenerateTypologyTests,PlanLayoutsTests,GeneratePrototypesTests}.cs; board/features/contracts/attempts/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P4-14, P2-19, P10-16 (relay194; P4-14 and P1-18 share tests/integration/test-catalogue-session.ps1 — serialize); then P1-18/P1-22, P2-20, P6-17/P6-18 (relay195; P6-17/P6-18 share PDF fixtures — serialize); then P3-15, P5-17/P7-16, P7-17 (relay197; P5-17 and P7-16 share the generate-typology parser — serialize). Owner: ADR0010/ADR0011; one -Catalogue run.
```
