# Relay156: drift resolution plan, housing typology pack, add-in install

Cloud scheduled relay, started 2026-11-04 22:53Z. GitHub CI was the only oracle (no local .NET SDK or PowerShell). The oldest-touched phases were 2, 5 and 10 (relay152); P2-07, P5-07 and P10-07 were picked (headless, no runtimes/revit-native, no overlap with the owner chat's chat-* branches). Each task went red in CI, then green, then through an independent read-only review: P2-07 had one blocker (re-plans after new drift reused operation ids, so PlanRunner would replay and write nothing) and four should-fix items; P5-07 three should-fix; P10-07 three should-fix. All closed with tests. The container restarted once mid-run; the lock was kept and heartbeated.

```
IMPLEMENTADO:
- P2-07 (Phase 2): MirrorDriftResolve — from a P2-04 drift report, per drifted element an adopt proposal (BIM-IR op from the mirror, proposed provenance) and a restore proposal (catalogue steps validated by RevitCommandProtocol, one ordered restore plan); never applied; ids follow the observed Revit state; typed unsupported codes (dependents_remain, geometry_invalid, …).
- P5-07 (Phase 5): pack bim.typology.vivienda-unifamiliar + SkillTypology.Resolve → room programme and ProjectBrief for family size 1–8; typed unknown kinds and minimum conflicts.
- P10-07 (Phase 10): scripts/install-addin.ps1 — install/uninstall Bim.addin from a verified release package into an explicit Addins folder, backup and restore, ownership by hash.
- Board: P2-07, P5-07, P10-07 ✅; new P2-08 drift on CLI/MCP, P2-09 selective resolution, P10-08 release archive with checksums, P10-09 audit verification and summary.
TESTEADO AUTOMÁTICAMENTE (backend):
- P2-07 tests/mirror-journal: red 41930130294, green 41931899995; review red 41933672582, green 41934750332. 0 failed attempts.
- P5-07 tests/skills: red 41929913861, green 41931202221; review red 41935947819, green 41937157242. 0 failed attempts.
- P10-07 tests/packaging (ubuntu + windows): red 41929591249, green 41930792081; review red 41938591628, green 41938674297. 0 failed attempts.
- The merged integration head runs CI again before main moves.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session this relay).
BLOQUEADO / POR QUÉ: nothing new blocked.
DECISIONES QUE NECESITO DE VOS: still open — escritorio/estudio/biblioteca class; Uniclass CC BY-ND excerpt; party-wall facade edges (P7-05). New, optional: the vivienda programme values are illustrative — confirm or correct them when convenient.
BENCHMARKS / NÚMEROS REALES: none measured.
GAPS CONOCIDOS:
- P2-07: not on CLI/MCP (P2-08); no selective apply (P2-09); deletes carry no confirm_dependents; replace changes UniqueIds without a map update; levels cannot be created/replaced.
- P5-07: illustrative values; only the effective composed minimum is compared; not wired to Bim.Generative.
- P10-07: package lacks Bim.Revit.dll; never loaded by Revit; Revit-running check absent.
ARCHIVOS CAMBIADOS: core/Bim.Core/MirrorDriftResolve.cs; tests/mirror-journal/{MirrorDriftResolveChecks,Program}.cs; skills/bim.typology.vivienda-unifamiliar/skill.json; core/Bim.Core/SkillTypology.cs; tests/skills/{TypologySkillTests,Program}.cs; scripts/install-addin.ps1; tests/packaging/{test-install-addin,test-package}.ps1; tasks.md; docs/features.json; docs/contracts/module-contracts.md; docs/agent/ATTEMPTS.md; docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); dotnet run --project tests/mirror-journal; dotnet run --project tests/skills; pwsh -NoProfile -File tests/packaging/test-package.ps1 -Dotnet dotnet.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P1-09/P1-10/P1-11, P3-05, P8 next batch, P9-06 port/P9-09/P9-03 (relay153); then P4-06/P4-07, P6-08/P6-09, P7-07 (relay155); then P2-08/P2-09, P5-08, P10-08/P10-09.
```
