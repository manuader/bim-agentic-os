# Relay152: provenance ledger, jurisdiction registry, style defaults into the intent compiler

Cloud scheduled relay, started 2026-11-04 11:53Z. GitHub CI was the only oracle (no local .NET SDK). The oldest-touched phases were 2, 5 and 10 (relay149); P2-06, P5-04 and P10-06 were picked (all headless, no Revit command files, no overlap with the owner chat's chat-* branches). Each task went red in CI, then green, then through an independent read-only review: P2-06 and P5-04 had one blocker each, closed with tests and confirmed closed by a re-review; P10-06 had none. All should-fix items were closed with tests.

```
IMPLEMENTADO:
- P2-06 (Phase 2): ProvenanceLedger — append-only, SHA-256 hash-chained record of where each BIM-IR element came from (intent + rule, catalogue command + operation_id, external edit from the mirror journal, CAD import), query by element/source, idempotent replay, conflict refusal, typed load refusals, size caps.
- P10-06 (Phase 10): JurisdictionRegistry + JurisdictionValidator — selects the code pack by jurisdiction code or country + municipality via new skills/<id>/jurisdiction.json sidecars (skill.json unchanged), refuses unknown/ambiguous/conflicting jurisdictions, envelope with pack versions, stub flags and source_confidence counts. Fictitious stub pack xx.example.codigo-ejemplo proves multi-jurisdiction selection; stub packs are refused unless the caller opts in.
- P5-04 (Phase 5): IntentSkillDefaults — style defaults fill open intent fields before the unchanged intent compiler, with per-field provenance; explicit values win; storey-based level elevations; typed refusals instead of silent zeros.
- Board: P2-06, P5-04, P10-06 ✅; new Phase 5 batch P5-07 (housing typology pack) and P5-08 (project skill lock file), both ⏳.
TESTEADO AUTOMÁTICAMENTE (backend):
- P2-06 tests/provenance (new gate provenance-tests): red 41825378485, green 41828338507 (1 failed attempt, oracle helper); review red 41830562019, green 41830824151.
- P10-06 tests/skills JurisdictionTests: red 41826892892, green 41828339068; review red 41832627501, green 41833656639. 0 failed attempts.
- P5-04 tests/intent SkillDefaultsTests: red 41825281521, green 41826002911; review red 41835551718, green 41836125195/41837332397. 0 failed attempts.
- The merged integration head runs CI again before main moves.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session this relay).
BLOQUEADO / POR QUÉ: nothing new blocked.
DECISIONES QUE NECESITO DE VOS: still open — escritorio/estudio/biblioteca class; Uniclass CC BY-ND excerpt; party-wall facade edges (P7-05). Nothing new.
BENCHMARKS / NÚMEROS REALES: provenance ledger caps default 100,000 entries / 64 MiB; no timing benchmarks.
GAPS CONOCIDOS:
- P2-06: in-memory only, not called by CLI/MCP/compiler/CAD; unkeyed chain (tail truncation needs an external head hash).
- P10-06: not wired to the brief or CLI/MCP; no JSON Schema for the sidecar; one pack per code.
- P5-04: draft has no JSON form; shipped styles lack door.height_m and wall.interior.type; clear height only validates.
ARCHIVOS CAMBIADOS: core/Bim.Core/{Provenance,ProvenanceLedger,Jurisdiction,JurisdictionRegistry,JurisdictionValidator,IntentSkillDefaults,IntentSkillDefaultsFill}.cs; tests/provenance/{Bim.Provenance.Tests.csproj,Program.cs}; tests/skills/{JurisdictionTests,Program}.cs; tests/intent/{SkillDefaultsTests,Program}.cs; skills/xx.example.codigo-ejemplo/{skill,jurisdiction}.json; skills/ar.caba.codigo-edificacion/jurisdiction.json; scripts/verify.ps1; docs/contracts/module-contracts.md; tasks.md; docs/features.json; docs/agent/ATTEMPTS.md; docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); dotnet run --project tests/provenance; dotnet run --project tests/skills; dotnet run --project tests/intent.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — Phase 1 P1-08, Phase 8 P8-06, Phase 9 P9-06 port/P9-07/P9-09/P9-03 (relay150; check origin chat-* branches first); then P3-05, P4-05, P6-07, P7-06, P2-07, P10-07, P5-07, P5-08.
```
