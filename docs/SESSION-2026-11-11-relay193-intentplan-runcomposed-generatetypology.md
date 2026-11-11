# Relay193: shared intent plan, TypologyPipeline.RunComposed, generate-typology CLI

Cloud scheduled relay, started 2026-11-11 21:52Z (LOCK was FREE after relay192). GitHub CI was the only oracle (no local .NET SDK or pwsh). Oldest-touched phases with ready work: 3, 5 and 7 (relay190). Workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (0 blockers, 5 should-fix, nits), every finding closed with tests, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P3-13 (Phase 3): interfaces/cli/IntentPlan.cs — ParseIntent + Build shared by apply-intent and diff-intent (parse, FromStored, OrderOf diff, typed refusal mapping); both verbs byte-identical; static checks require the helper and the apply-intent order ParseIntent → stale check → Build → Commit.
- P5-15 (Phase 5): TypologyPipeline.RunComposed(pack, request, options, skills?, precedence?) — callers without a SkillResolution get the composed-rules path; content-equal manifests deduped; typed typology_kind_mismatch / id_duplicate (deterministic) / resolver issues; pin a8c62da5… unchanged.
- P7-14 (Phase 7): `generate-typology request.json output.json` — strict bounded request, packs via SkillPackLookup (package first), TypologyPipelineSerializer bytes byte-equal to in-process Run, never-overwrite, typed generate_typology_* refusals.
- Board: P3-13, P5-15, P7-14 ✅; new P3-14 (MCP diff_intent tool), P5-16 (generate-typology composed rules from extra manifests), P7-16 (generate-typology include and code_pack). Progress 156/175 (89.1 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P3-13 tests/cli: red 43107221958, green 43109253148; review green 43112753940. 0 failed attempts.
- P5-15 tests/generative: red 43107368116, green 43109205242; review red 43115474002/43115509389, green 43117947248. 0 failed attempts.
- P7-14 tests/cli: red 43107527837, green 43109424365; review green 43120709470. 0 failed attempts.
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; P4-11 / P1-14 owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. Optional: repository licence.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P3-13: equality by construction enforced by static text checks plus the existing CLI byte tests (the CLI test project cannot call the internal helper); no MCP tool (→ P3-14).
- P5-15: catalog is only the given manifests; programme pack id "" on a refused resolution; no CLI (→ P5-16).
- P7-14: fixed code pack; no include/code_pack/composed members (→ P7-16, P5-16); project_id required; packaged verb not exercised by tests/packaging; dangling-symlink race at the output path reports output_io_error (shared GltfExporter helper, safe).
ARCHIVOS CAMBIADOS: interfaces/cli/{IntentPlan,ApplyIntentCommand,DiffIntentCommand,GenerateTypologyCommand,Program}.cs; core/Bim.Generative/TypologyPipeline.cs; tests/cli/{ApplyIntentTests,DiffIntentTests,GenerateTypologyTests,Program}.cs; tests/generative/{TypologyPipelineManifestsTests,Program}.cs; board/features/contracts/attempts/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P4-14/P4-15, P2-17, P10-15 (relay191; P4-14 and P1-18 share tests/integration/test-catalogue-session.ps1 — serialize); then P1-18/P1-21/P1-22, P2-18, P6-16/P6-17 (relay192; P1-21 before P1-22); then P3-14, P5-16/P7-16, P7-15 (relay193; P5-16 and P7-16 share GenerateTypologyCommand — serialize). Owner: ADR0010/ADR0011; one -Catalogue run.
```
