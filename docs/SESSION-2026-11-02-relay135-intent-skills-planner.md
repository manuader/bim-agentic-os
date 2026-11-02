# Relay135: multi-room intent, skill manifests and space planner

This was a cloud scheduled relay on 2026-11-02, started by a manual test fire from the owner's chat. The access check passed: git clone and push to manuader/bim-agentic-os worked, and api.github.com answered HTTP 200. There is no local .NET SDK, so GitHub CI was the only oracle. Every task followed the same path: red in CI, green in CI, an independent read-only review, review fixes closed with tests, and green again.

```
IMPLEMENTADO:
- P3-01 (Phase 3): a multi-room layout intent AST with a deterministic compiler to the existing BIM-IR records. Shared walls are emitted once and ids are stable. It checks thick-body wall clashes and opening clear spans, and a one-room edit gives a minimal diff. Canonical JSON. No schema change.
- P5-01 (Phase 5): a versioned domain-skill manifest, a strict parser, a loader that refuses links, and an iterative resolver whose resolved defaults stay traceable. There are two illustrative sample skills, plus docs/contracts/skill.schema.json, which a jsonschema corpus checks in CI.
- P7-01 (Phase 7): core/Bim.Generative, a seeded deterministic rectangular space planner driven by the ProjectBrief. Results are typed and it never throws.
TESTEADO AUTOMÁTICAMENTE (backend):
- tests/intent: 144 checks, red 41530098967, green 41530804027.
- tests/skills: about 200 checks plus a 76-case schema corpus, red 41533053459, green 41533653759.
- tests/generative: about 103 checks, red 41535328332, green 41535913478.
- All on Ubuntu and Windows. The merged integration head ran CI again before main moved.
VERIFICADO EN REVIT NATIVO: nothing new.
BLOQUEADO / POR QUÉ: C04/P1-04 catalogue re-run and the N21 third run, which need the owner's Revit session.
DECISIONES QUE NECESITO DE VOS: none. jsonschema 4.23.0 (MIT) was added as a CI-only check, under the pre-approved permissive-licence rule.
BENCHMARKS / NÚMEROS REALES: none measured (the planner's 30-room case has a <10 s test bound only).
GAPS CONOCIDOS:
- The planner only produces slicing floorplans with no corridors, and search_exhausted does not prove a brief is infeasible.
- Skill rules are resolved but not yet evaluated.
- door and window kinds have no GeometryCodec arity.
- The intent compiler does not yet produce Revit commands (P3-02).
ARCHIVOS CAMBIADOS: core/Bim.Core/Intent*.cs, Skill*.cs; core/Bim.Generative/**; skills/**; docs/contracts/skill.schema.json; tests/intent, tests/skills, tests/generative; scripts/verify.ps1 (wiring); .github/workflows/ci.yml (schema step); tasks.md; docs/features.json.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); python -I tests/skills/check_schema.py (jsonschema).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: P3-02 compiler→catalogue bridge (turns layouts into Revit commands), P5-02 CABA rule pack (feeds the planner), P7-02 alternatives.
```
