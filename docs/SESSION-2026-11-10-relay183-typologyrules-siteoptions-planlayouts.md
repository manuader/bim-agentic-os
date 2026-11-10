# Relay183: composed typology rules, cad-plan-brief site options, plan-layouts verb

Cloud scheduled relay, started 2026-11-10 18:53Z (LOCK was FREE after relay181). GitHub CI was the only oracle (no local .NET SDK or pwsh). Oldest-touched phases with ready work: 5, 6 and 7 (relay176). Workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (0 blockers, 6 should-fix), every should-fix and the cheap nits closed with tests, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P5-13 (Phase 5): TypologyRules.From/Generate — composed error-severity area/side minimums of a SkillResolution become the typology SpacePlanner rules with per-pack/rule-id traces; corridor rule drives the hall; side² above the programme maximum refused (typology_minimum_conflict); shared internal TypologyBrief.Plan (no duplicated checks, refusal parity table); defaults reproduce the P5-11 digest.
- P6-12 (Phase 6): `cad-plan-brief ... [--site-width w --site-depth d] [--name text]` — bounded site [3,100] m replaces only constraints.site; the written brief is planned by SpacePlanner byte-equal to in-process; absent options byte-identical; typed cad_option_* (new cad_option_pair_missing).
- P7-11 (Phase 7): `plan-layouts brief.json output.json [--alternatives n] [--seed s]` — AlternativeGenerator + AlternativeRanker, canonical AlternativeSet never-overwrite, typed plan_layouts_* envelopes, byte-equal to in-process.
- CLI usage text lists the new flags and verb. Board: P5-13, P6-12, P7-11 ✅; P6-13 ⏳ (deps done); new P1-19 (flaky fake pipe), P7-13 (plan-layouts --pack). Progress 129/150 (86.0 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P5-13 tests/generative: red 42880542772, green 42882779700; review red 42889236815, green 42889460850. 1 failed attempt (see ATTEMPTS).
- P6-12 tests/cli: red 42882221368, green 42884390488; review green 42891766974. 0 failed attempts.
- P7-11 tests/cli: red 42880375102, green 42882133062; review green 42894097712. 0 failed attempts.
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A); P5-13 semantics — only error-severity pack rules constrain the planner (warnings ignored); confirm or change.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P5-13: composed area minimums above the programme minimum are still refused (P5-07), so mostly side rules change plans; hall special-cases CABA corredor-side-min 1.00 m; no CLI; not through pipeline gates (P5-12).
- P6-12: site given by hand, no provenance; de-DE culture check may SKIP on runners without culture data.
- P7-11: no pack rules (P7-13); default alternatives 4.
- Flaky catalogue-session-cli `revit-command --all` pipe_io_error on Linux (P1-19) blocks the later Linux cli steps when it fires.
ARCHIVOS CAMBIADOS: core/Bim.Generative/{TypologyRules,TypologyBrief}.cs, tests/generative/{TypologyRulesTests,Program}.cs, interfaces/cli/{CadPlanBriefCommand,PlanLayoutsCommand,Program}.cs, tests/cli/{CadPlanBriefTests,PlanLayoutsTests,Program}.cs; board/features/contracts/attempts/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); dotnet run --project tests/generative; dotnet run --project tests/cli.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P3-10, P10-12/P10-13 (relay178); then P4-11, P1-16/P1-17/P1-19, P2-13 (relay181); then P5-12, P6-13, P7-12/P7-13 (relay183). Owner: accept ADR0010/ADR0011; one -Catalogue run.
```
