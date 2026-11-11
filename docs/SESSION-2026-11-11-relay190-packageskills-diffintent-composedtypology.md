# Relay190: generate-prototypes from the package, diff-intent, composed-rules typology pipeline

Cloud scheduled relay, started 2026-11-11 10:52Z (LOCK was FREE after relay188). GitHub CI was the only oracle (no local .NET SDK or pwsh). Oldest-touched phases with ready work: 7, 3 and 5 (relay186). Workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (0 blockers, 6 should-fix, nits), every finding closed with tests, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P7-12 (Phase 7): the release package ships every tracked skills/ file next to cli/ (manifest/checksums cover them; non-regular tracked entries refused skills_entry_not_regular); generate-prototypes looks in <package>/skills first when the binary folder is cli/ beside VERSION, manifest.json and skills/, then the repository walk; byte-equal package and repo runs; package pack wins; stray skills/ ignored; missing skills still refuses generate_prototypes_repository_not_found. plan-layouts --pack unchanged (repository-only).
- P3-12 (Phase 3): `diff-intent --project p --intent i` read-only plan, byte-equal to apply-intent's plan member (incl. out-of-compile-order heads), writes nothing, typed diff_intent_* refusals cross-checked against apply-intent, de_DE identical.
- P5-14 (Phase 5): opt-in TypologyPipelineOptions(ComposedRules) plans with the P5-13 composed TypologyRules; rules recorded in JSON; snap uses the composed rules; typology_pipeline_pack_mismatch; default bytes unchanged (both P5-12 pins); composed pin a8c62da5… on Linux and Windows.
- Board: P7-12, P3-12, P5-14 ✅; new P3-13 (shared intent-plan helper), P5-15 (composed rules from a manifest list), P7-15 (plan-layouts --pack from the package). Progress 147/165 (89.1 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P7-12 tests/cli + tests/packaging: red 43007834744, green 43009419027; review red 43013468121, green 43014850081. 0 failed attempts.
- P3-12 tests/cli: red 43007872834, green 43009808461; review green 43017474972. 0 failed attempts.
- P5-14 tests/generative: red 43007881318, pin 43008867735, green 43010911469; review red 43020311373, green 43021645045. 0 failed attempts.
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; P4-11 / P1-14 owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. Optional: repository licence.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P7-12: package root detected by marker files, not content; new packaging checks never observed red (verify.ps1 stops at the first failing gate); ships all tracked packs incl. xx.example.
- P3-12: plan logic duplicated from apply-intent (→ P3-13); de_DE check may skip without culture data.
- P5-14: composed rules only through the SkillResolution overload (→ P5-15); pack check Id/Version only; no CLI (P7-14).
ARCHIVOS CAMBIADOS: interfaces/cli/{SkillPackLookup,GenerateCommand,PlanLayoutsCommand (comment),DiffIntentCommand,Program}.cs; scripts/package.ps1; tests/cli/{GeneratePrototypesTests,DiffIntentTests,Program}.cs; tests/packaging/test-package.ps1; core/Bim.Generative/TypologyPipeline{,Model,Options,Serializer}.cs; tests/generative/{TypologyPipelineRulesTests,Program}.cs; board/features/contracts/attempts/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P4-13/P4-14, P10-14 (relay187; P4-14 and P1-18 share tests/integration/test-catalogue-session.ps1 — serialize); then P1-18/P1-20/P1-21, P2-15/P2-16, P6-15 (relay188; P1-20 and P1-21 share the revit-command flag parser — serialize); then P3-13, P5-15, P7-14/P7-15 (relay190; P7-14 and P7-15 share SkillPackLookup — serialize). Owner: ADR0010/ADR0011; one -Catalogue run.
```
