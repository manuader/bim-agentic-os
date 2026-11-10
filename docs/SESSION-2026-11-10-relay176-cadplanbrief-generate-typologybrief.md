# Relay176: cad-plan-brief and generate-prototypes CLI verbs, typology programme to prototypes

Cloud scheduled relay, started 2026-11-10 07:52Z (LOCK was FREE after relay174). GitHub CI was the only oracle (no local .NET SDK or pwsh). Oldest-touched phases 6, 7 (relay168) and 2 (relay169): P6-10, P7-09 and P2-11. P2-11 turned out blocked by the persisted provenance ledger format, so ADR0010 was written `proposed` and P5-11 (phase 5, relay169) took its slot. Workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (0 blockers, 8 should-fix), every should-fix and the cheap NITs closed with tests, integration head re-run in CI before main moved. Phases 5, 6 and 7 were then complete on the board, so their next batches were loaded.

```
IMPLEMENTADO:
- P6-10 (Phase 6): `cad-plan-brief draft.json output.json [--min-confidence x] [--confirm id]... [--confirm-reason code]...` — BriefPlanner.ToPlannerBrief over a cad-brief document; strict members, work bound, label re-binding so duplicate-label joins match in-process; canonical never-overwrite output; invalid planner brief written with valid:false (exit 0).
- P7-09 (Phase 7): `generate-prototypes request.json output.json` — PrototypePipeline.Run from a strict snake_case request and a skills/<pack> manifest; derived v8 project id from the canonical request; never-overwrite; typed refusals; repo-only for now.
- P5-11 (Phase 5): TypologyBrief — SkillTypology programme (pack + family size) → planner brief → ranked whole-lot prototypes without hand-written briefs; pinned digest.
- P2-11 (Phase 2): blocked; ADR0010 (drift-adopt provenance) proposed; P2-12 (commit adopted drift without provenance) split out and ready.
- Board: P5-11, P6-10, P7-09 ✅; P2-11 ⛔ (ADR0010); new P2-12, P5-12, P5-13, P6-12, P6-13 ⛔, P7-11, P7-12. CLI usage text lists both new verbs.
TESTEADO AUTOMÁTICAMENTE (backend):
- P6-10 tests/cli (ubuntu/windows/macOS): red 42780498633, green 42784502068; review red 42793407231, green 42797233472. 2 failed attempts (warnaserror nullability; test helper on dangling symlink).
- P7-09 tests/cli: red 42778985107, green 42781456410; review red 42800136898, green 42801508178. 1 failed attempt (package.ps1 source list).
- P5-11 tests/generative: red 42787338649, green 42788507940; review red 42803818216, green 42805199628. 0 failed attempts.
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 — the P2-06 ledger cannot record update/delete adopts nor a proposal id without a persisted-format change (ADR0010 proposed, options A/B/C/defer).
DECISIONES QUE NECESITO DE VOS: ADR0010 (recommended A: new `drift_adopt` ledger kind); still open — escritorio/estudio/biblioteca class; Uniclass excerpt licence; party-wall facade edges; P9-03 free-path ADR acceptance.
BENCHMARKS / NÚMEROS REALES: none measured.
GAPS CONOCIDOS:
- P6-10: no site/name options (P6-12); unknown top-level members accepted; invalid brief exits 0 by design.
- P7-09: packaged CLI has no skills/ (P7-12); stage options not exposed; id covers pack id not manifest content; exponent-form canonical numbers untested.
- P5-11: whole-lot, one level; not through circulation/facade/IR gates (P5-12); planner rules are the fixed CABA ones (P5-13).
ARCHIVOS CAMBIADOS: interfaces/cli/{CadPlanBriefCommand,GenerateCommand,Program}.cs, interfaces/cli/Bim.Cli.csproj, tests/cli/{CadPlanBriefTests,GeneratePrototypesTests,Program}.cs, tests/cli/Bim.Cli.Tests.csproj, scripts/package.ps1, core/Bim.Generative/TypologyBrief.cs, tests/generative/{TypologyBriefTests,Program}.cs, docs/architecture/adr/0010-drift-adopt-provenance.md; board/features/contracts/attempts/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); dotnet run --project tests/cli; dotnet run --project tests/generative.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P10-11 (relay169); P8/P9 (relay171; P9-03 ADR still to write as proposed); P1-14/P1-15, P3-09, P4-10 (relay174); then P2-12, P5-12/P5-13, P6-12, P7-11/P7-12.
```
