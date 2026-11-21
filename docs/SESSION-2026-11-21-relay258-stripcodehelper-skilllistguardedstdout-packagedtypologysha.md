# Relay258: StripCode helper, skill-list guarded stdout, packaged typology/layout sha

Relay258 (cloud scheduled, 2026-11-21 05:52Z) took the FREE LOCK, picked the oldest-touched phases 1, 5 and 7 (relay255), got three workers green, ran one independent read-only review of all three branches (all approve, 0 blockers; nits recorded as follow-up tasks) and integrated through one integration head run in CI before main moved. CI is the only oracle (no local .NET SDK).

```
IMPLEMENTADO:
- P1-39 (Phase 1): test-only — StripComments and StripCommentsAndLiterals merged into one StripCode helper (interpolation holes read as code, nested literals/comments/braces, raw $$); member bodies by brace matching (no 4-space dependency); mutation self-test; 3e D2 control proves the StreamReader-frame filter (Linux/macOS IOException, Windows completed null); Windows B pinned as measured "completed null" (.NET 8.0.34); step E relabelled.
- P5-33 (Phase 5): DEFECTS FIXED — skill-list with a throwing stdout escaped Run (exit 0, crash in a real process) → guarded print, exit 1; jurisdiction-resolve with a throwing stderr escaped before the typed line → shared guarded WriteDiagnostic. Real-process 2>&-, 2>/dev/full, >/dev/full runs; documented deltas; shared tests/cli/RejectingWriter.cs.
- P7-35 (Phase 7): test-only — packaged generate_typology (infeasible, exact status) and plan_layouts documents pinned by sha256, CLI == MCP; one cleanup warning helper; root-safe named skip; $p733Os macOS; catalogue-pin note in contracts.
- Board: P1-39, P5-33, P7-35 ✅; new P1-41, P5-35, P7-37. Progress 309/328 (94.2 %). features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P1-39: probe red 44873211457, green 44874717493. 0 failed attempts.
- P5-33: red 44873393542, green 44874530614. 0 failed attempts.
- P7-35: red 44873131145, green 44875399800. 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged; optional P6-35 skip-the-form choice, unchanged.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P1-39: global:: inside a hole drops the call, expression-bodied MemberBody, section 2d still searches 4-space method end; Windows pins are runtime behaviour → P1-41.
- P5-33: MCP jurisdiction_resolve tool writes stderr bare; jurisdiction-resolve stdout unguarded; WriteDiagnostic lives in SkillListCommand; real-process internal error unreachable (in-process only) → P5-35. File-scope deviations recorded on the row (section 13, tests/mcp SkillListToolTests/JurisdictionResolveToolTests/csproj).
- P7-35: no ranked packaged typology pin, P7-12 cleanup not via helper, plan_layouts status regex, byte lengths not asserted, macOS never runs test-package.ps1 → P7-37.
ARCHIVOS CAMBIADOS: tests/cli/RevitCommandDeadlineTests.cs; interfaces/cli/SkillListCommand.cs, interfaces/cli/JurisdictionResolveCommand.cs, tests/cli/SkillListTests.cs, tests/cli/RejectingWriter.cs, tests/mcp/SkillListToolTests.cs, tests/mcp/JurisdictionResolveToolTests.cs, tests/mcp/Bim.Mcp.Tests.csproj; tests/packaging/test-package.ps1, docs/contracts/module-contracts.md; tasks.md, docs/features.json, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P2-38, P3-29, P3-32, P4-32 (relay256); then P10-35, P10-36, P6-34, P6-35, P7-36 (relay257); then P1-40, P1-41, P5-34, P5-35, P7-37.
```
