# Relay186: typology pipeline, plan-layouts --pack, apply-intent

Cloud scheduled relay, started 2026-11-11 01:52Z (LOCK was FREE after relay185). GitHub CI was the only oracle (no local .NET SDK or pwsh). Oldest-touched phases with ready work: 5 and 7 (relay183), then 3 (relay184). Phase 9 has no ready work (P9-03 waits for ADR0011). Workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (0 blockers, 8 should-fix), every finding closed with tests, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P5-12 (Phase 5): TypologyPipeline.Run runs TypologyBrief candidates through the unchanged PrototypePipeline circulation → facade → BIM-IR window gates and ranking (request weights, strategy whole_lot); canonical serializer; two pinned digests (Infeasible 12x12 family 4; Ranked 16x16 family 3).
- P7-13 (Phase 7): `plan-layouts ... --pack id` plans with the pack's min area/side rules (grouped per glob, traced); shared SkillPackLookup (generate-prototypes refactored onto it unchanged, id traversal guard); typed plan_layouts_pack_* refusals.
- P3-11 (Phase 3): `apply-intent --store s --intent after.json [--expected-revision n]` over IntentEdits.FromStored + IncrementalDiff (OrderOf placement) and the shared apply-edits commit path.
- Board: P5-12, P7-13, P3-11 ✅; new P3-12 (diff-intent read-only verb), P5-14 (TypologyPipeline with composed rules), P7-14 (generate-typology CLI). P3-11 Done-when reworded to the checked OrderOf semantics. Fixed an unescaped `|` in the P2-13 row (relay185) that made the Progress parser throw. Progress 138/157 (87.9 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P5-12 tests/generative: red 42942656762, green 42945770818; review green 42949856832. 0 failed attempts.
- P7-13 tests/cli: red 42942742275, green 42943881517; review red 42951916888, green 42952935652. 0 failed attempts.
- P3-11 tests/cli: red 42942878532, green 42944305451; review green 42954721581. 0 failed attempts.
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; P4-11 / P1-14 owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P5-12: plans with fixed PrototypeTemplates.Rules (P5-14); on 12x12/16x16 many candidates are duplicates or fail facade_ir_entrance_overlap / facade_edge_too_short@dining; resolution overload does not check options.Pack against the resolution.
- P7-13: one pack only (requires not resolved); minimums above a brief maximum give an infeasible set, not a refusal; de_DE checks skip silently without culture data.
- P3-11: plan differs from diff-edits in op order/AfterHash when a create sorts before a survivor (documented, tested); a no-change rerun without --expected-revision commits an empty revision; FromStored deletes head elements the intent does not produce.
ARCHIVOS CAMBIADOS: core/Bim.Generative/{TypologyPipeline,TypologyPipelineModel,TypologyPipelineSerializer,PrototypePipeline}.cs; interfaces/cli/{PlanLayoutsCommand,SkillPackLookup,GenerateCommand,ApplyIntentCommand,ApplyEditsCommand,Program}.cs; tests/generative/{TypologyPipelineTests,Program}.cs; tests/cli/{PlanLayoutsTests,ApplyIntentTests,ApplyEditsTests,Program}.cs, tests/cli/Bim.Cli.Tests.csproj; board/features/contracts/attempts/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P4-12/P4-13, P10-13 (relay184); then P1-16/P1-17/P1-18, P2-14/P2-15, P6-14 (relay185); then P7-12 (P7-12 touches GenerateCommand.cs, now using SkillPackLookup — package-root lookup belongs in SkillPackLookup.cs), P3-12, P5-14, P7-14 (relay186; P7-14 depends on SkillPackLookup too — serialize with P7-12). Owner: ADR0010/ADR0011; one -Catalogue run.
```
