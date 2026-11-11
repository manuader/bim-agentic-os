# Relay188: MCP query.elements all, drift loop end to end, cad-plan-brief --brief-only

Cloud scheduled relay, started 2026-11-11 07:53Z (LOCK was FREE after relay187). GitHub CI was the only oracle (no local .NET SDK or pwsh; dot.net blocked by the proxy). Oldest-touched phases with ready work: 1, 2 and 6 (relay185). Workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (0 blockers, 5 should-fix, nits), every finding closed with tests, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P1-16 (Phase 1): MCP `revit_query_elements` optional boolean `all` → RevitElementQueryPaging.All over the same transport, ElementQueryAllResult JSON byte-equal to CLI `revit-command … --all` on a real fake pipe (under MCP caps); MCP-side overall deadline (query_all_deadline_exceeded, 1800 s) and 32 MiB byte cap for the long-lived server; read label unchanged; absent flag keeps bytes.
- P2-14 (Phase 2): end-to-end drift loop over the real CLI and the pwsh fake add-in: external move → mirror-drift → select → adopt (revision 2) → second mirror-drift reports no drift; byte-identical across two runs; fake request log asserted; BIM_PWSH from verify.ps1 so a missing pwsh fails instead of skipping.
- P6-14 (Phase 6): `cad-plan-brief --brief-only` writes the bare planner brief (exact `brief` member bytes) that plan-layouts reads directly; DXF→layouts chain no longer needs a copy step (digest unchanged); default file and envelope bytes pinned.
- Board: P1-16, P2-14, P6-14 ✅; P6-15 ⏳; new P1-21 (CLI --all overall deadline), P2-16 (drift loop with journal + create/delete adopts). Progress 144/162 (88.9 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P1-16 tests/mcp: red 42983336938, green 42986703668; review red 42994986316, green 42997716419. 2 failed attempts (ATTEMPTS).
- P2-14 tests/mirror-journal: red 42983599263 (deliberate), green 42985259803; review green 42999991673. 0 failed attempts.
- P6-14 tests/cli: red 42983360377, green 42984926046; review red 42989848662, green 42992146562. 0 failed attempts.
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; P4-11 / P1-14 owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. Optional: repository licence.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P1-16: MCP caps tighter than CLI (byte-equality only under them); injected-transport deadline checked between pages; MCP does not read owner policy files (CLI does).
- P2-14: no journal (findings unknown), update adopt only (→ P2-16); fake add-in only.
- P6-14: bare output drops kept/dropped/issues.
ARCHIVOS CAMBIADOS: interfaces/mcp/McpServer.cs; tests/mcp/Program.cs; tests/mirror-journal/{MirrorDriftLoopChecks,Program}.cs; scripts/verify.ps1 (BIM_PWSH line); interfaces/cli/{CadPlanBriefCommand,Program}.cs; tests/cli/{CadPlanBriefTests,CadToLayoutsTests}.cs; board/features/contracts/attempts/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P7-12, P3-12, P5-14, P7-14 (relay186; P7-12/P7-14 share SkillPackLookup — serialize); then P4-13/P4-14, P10-14 (relay187; P4-14 and P1-18 share tests/integration/test-catalogue-session.ps1 — serialize); then P1-18/P1-20/P1-21, P2-15/P2-16, P6-15 (relay188; P1-20 and P1-21 share the revit-command flag parser — serialize). Owner: ADR0010/ADR0011; one -Catalogue run.
```
