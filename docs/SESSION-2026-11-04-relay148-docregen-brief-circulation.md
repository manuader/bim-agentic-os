# Relay148: affected-only documentation regeneration, brief extraction from drawings, circulation

Cloud scheduled relay, started 2026-11-04 00:52Z. GitHub CI was the only oracle (no local .NET SDK). The oldest-touched phases were 4 (relay142), 6 and 7 (relay144); all three tasks are headless, so nothing overlapped the owner chat's Revit command work. P9-06 stays waiting for the owner's answer on chat-materials-types. Each task went red in CI, then green, then through an independent read-only review; P4-03 and P6-05 needed a second review round (blockers), all findings closed with tests. A container restart interrupted the P6-05 worker after its first fix push; the controller followed its CI and resumed it.

```
IMPLEMENTADO:
- P4-03 (Phase 4): core/Bim.Core/DocRegeneration — caller-owned DocumentationSet (views, tags, wall dimensions, schedules, sheets, placements; optional BIM-IR binding) → Full catalogue commands with typed unsupported entries; Affected(before, after, Compiler.Diff) keeps only items whose own result differs, plus parent closure; Merge(Full(before), Affected) == Full(after); input digest (input_mismatch); host_ambiguous, doc_text_invalid, null refs typed.
- P6-05 (Phase 6): adapters/cad/Bim.Cad/BriefExtract — DXF/PDF room labels + es/en area texts → P6-03 brief draft (validator-clean) with per-room confidence, provenance and uncertainty (unlabelled-region, area-mismatch, duplicates, orphan-area, outer-region, uncalibrated, area-ambiguous, limit codes); MetresPerUnit; block furniture excluded; frame labels suppressed.
- P7-04 (Phase 7): core/Bim.Generative/Circulation* — door/entrance reachability graph, shortest routes, hard checks (entrance, reachable, bedroom_access, passage), opt-in CirculationRanker criterion on top of AlternativeRanker (existing digests unchanged).
- Board: new ⏳ P3-05 incremental diff, P4-04 default documentation set, P4-05 stale documentation replacement plan, P6-06 DXF ATTRIB/MTEXT, P6-07 CAD CLI verbs.
TESTEADO AUTOMÁTICAMENTE (backend):
- P4-03 tests/doc-regeneration (new project in verify.ps1): red 41751742782, green 41752470200; review reds 41755088266 / 41760520985, greens 41755567519 / 41761011983. 0 failed attempts.
- P6-05 tests/cad BriefExtractTests: red 41752281978, green 41752953004; review reds 41756681420 / 41761968917, greens 41756968524 / 41762098208. 0 failed attempts.
- P7-04 tests/generative CirculationTests: red 41751286259, green 41752337010; review red 41758412039, green 41758872020. 0 failed attempts.
- The merged integration head runs CI again before main moves.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session this relay).
BLOQUEADO / POR QUÉ: P9-06 waits for the owner's answer (integrate chat-materials-types from a relay, or the chat merges it).
DECISIONES QUE NECESITO DE VOS: (1) P9-06 chat-materials-types: relay reviews+merges, or the chat does? (2) still open: escritorio/estudio/biblioteca class; Uniclass CC BY-ND excerpt.
BENCHMARKS / NÚMEROS REALES: one wall move on a 50-room documentation set regenerates 2/404 items (item count, not wall time).
GAPS CONOCIDOS:
- P4-03: no delete commands emitted (P4-05 loaded); wall-only dimensions; fake transport only.
- P6-05: synthetic fixtures only; no ATTRIB (P6-06 loaded) nor CID-font PDF text; untuned confidence; gross region areas.
- P7-04: candidate doors on every shared wall; one entrance; level 0 only; PrototypeGenerator not gated.
ARCHIVOS CAMBIADOS: core/Bim.Core/DocRegeneration.cs, tests/doc-regeneration/**, scripts/verify.ps1; adapters/cad/Bim.Cad/BriefExtract*.cs, tests/cad/BriefExtractTests.cs, tests/cad/Program.cs, tests/cad/fixtures/brief-plan.dxf; core/Bim.Generative/Circulation*.cs, tests/generative/CirculationTests.cs, tests/generative/Program.cs; docs/contracts/module-contracts.md; tasks.md; docs/features.json; docs/agent/ATTEMPTS.md; docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: P9-06 once the owner answers; then P9-07/P9-08/P9-09 (coordinate with the owner chat), P9-03/P9-04, P8-06/P8-07, P1-07/P1-08, P2-05, P5-04/P5-05, P10-04, P7-05, P3-05, P4-04/P4-05, P6-06/P6-07.
```
